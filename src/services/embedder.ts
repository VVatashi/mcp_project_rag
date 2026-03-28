import { env, pipeline } from "@xenova/transformers";
import { EmbeddingFunction } from "chromadb";
import { AppConfig } from "../config.js";

type FeatureExtractor = (texts: string[] | string, options?: Record<string, unknown>) => Promise<{
  tolist(): unknown;
}>;

export class TransformersEmbeddingFunction implements EmbeddingFunction {
  public name = "transformers-local";
  private extractorPromise?: Promise<FeatureExtractor>;

  public constructor(private readonly config: AppConfig) {
    env.cacheDir = config.modelCacheDir;
    env.allowLocalModels = true;
  }

  public async generate(texts: string[]): Promise<number[][]> {
    const extractor = await this.getExtractor();
    const output = await extractor(texts, {
      pooling: "mean",
      normalize: true,
    });
    return output.tolist() as number[][];
  }

  public async generateForQueries(texts: string[]): Promise<number[][]> {
    return this.generate(texts);
  }

  public defaultSpace(): "cosine" {
    return "cosine";
  }

  private getExtractor(): Promise<FeatureExtractor> {
    if (!this.extractorPromise) {
      this.extractorPromise = pipeline(
        "feature-extraction",
        this.config.embeddingModel,
      ) as Promise<FeatureExtractor>;
    }

    return this.extractorPromise;
  }
}
