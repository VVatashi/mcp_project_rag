import { SearchHit } from "../domain.js";
import {
  cosineSimilarity,
  documentationIntentScore,
  exactTokenBoost,
  normalizeQuery,
  phraseMatchScore,
  termOverlapScore,
} from "../utils.js";
import { TransformersEmbeddingFunction } from "./embedder.js";

export class NeuralReranker {
  public constructor(private readonly embedder: TransformersEmbeddingFunction) {}

  public async rerank(query: string, hits: SearchHit[]): Promise<SearchHit[]> {
    if (hits.length === 0) {
      return [];
    }

    const normalizedQuery = normalizeQuery(query);
    const docsIntent = documentationIntentScore(normalizedQuery);
    const [queryEmbedding] = await this.embedder.generateForQueries([normalizedQuery]);
    const textEmbeddings = await this.embedder.generate(hits.map((hit) => hit.text));

    return hits
      .map((hit, index) => {
        const searchablePath = hit.sourcePath.replace(/[\\/._-]+/g, " ");
        const searchableSourceName = hit.sourceName.replace(/[\\/._-]+/g, " ");
        const structuralText = [
          searchablePath,
          searchableSourceName,
          hit.language,
          hit.kind,
          hit.text,
        ].join("\n");
        const neuralScore = cosineSimilarity(
          queryEmbedding ?? [],
          textEmbeddings[index] ?? [],
        );
        const lexicalScore = termOverlapScore(normalizedQuery, structuralText);
        const pathScore = Math.max(
          exactTokenBoost(normalizedQuery, searchablePath),
          phraseMatchScore(normalizedQuery, searchablePath),
        );
        const sourceNameScore = Math.max(
          exactTokenBoost(normalizedQuery, searchableSourceName),
          phraseMatchScore(normalizedQuery, searchableSourceName),
        );
        const phraseScore = phraseMatchScore(normalizedQuery, hit.text);
        const docIntentBoost =
          hit.kind === "doc"
            ? docsIntent * 0.2
            : docsIntent > 0 && (hit.sourcePath.startsWith("docs/") || hit.sourceName === "README.md")
              ? docsIntent * 0.1
              : 0;
        const metadataBoost =
          pathScore * 1 +
          sourceNameScore * 0.75 +
          (normalizedQuery.includes(hit.language.toLowerCase()) ? 0.15 : 0) +
          (normalizedQuery.includes(hit.kind.toLowerCase()) ? 0.05 : 0) +
          docIntentBoost;
        const finalScore =
          neuralScore * 0.4 +
          lexicalScore * 0.2 +
          phraseScore * 0.15 +
          hit.retrievalScore * 0.05 +
          metadataBoost * 0.2;

        return {
          ...hit,
          rerankScore: neuralScore * 0.7 + lexicalScore * 0.2 + phraseScore * 0.1,
          finalScore,
        };
      })
      .sort((left, right) => right.finalScore - left.finalScore);
  }
}
