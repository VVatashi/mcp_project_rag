using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tokenizers.DotNet;

namespace ProjectRAG.Cli;

/// <summary>Settings of the embedding model, bound from the Embedding section of appsettings.json.</summary>
public sealed class EmbeddingOptions
{
    public const string SectionName = "Embedding";

    public string TokenizerFile { get; set; } = "models/Qwen3-Embedding-0.6B/tokenizer.json";

    public string ModelFile { get; set; } = "models/Qwen3-Embedding-0.6B/onnx/model_quantized.onnx";

    /// <summary>The batch size that gives the best speed on the CPU for chunks of about 512 tokens.</summary>
    public int BatchSize { get; set; } = 8;
}

/// <summary>Turns texts into normalized vectors. A document and a query go through different methods because a model may ask for a prefix on the query side.</summary>
public interface IEmbeddingProvider
{
    /// <summary>The length of every vector this provider returns.</summary>
    int Dimensions { get; }

    /// <summary>The cosine similarity of two normalized vectors, which is their dot product.</summary>
    static float Similarity(float[] left, float[] right)
    {
        float sum = 0f;
        for (int i = 0; i < left.Length; i++)
            sum += left[i] * right[i];

        return sum;
    }

    float[] EmbedDocument(string text);

    float[] EmbedQuery(string query);

    /// <summary>Embeds many passages. The vectors keep the order of the input.</summary>
    ImmutableArray<float[]> EmbedDocumentBatch(IReadOnlyList<string> texts);

    /// <summary>Embeds many search queries. The vectors keep the order of the input.</summary>
    ImmutableArray<float[]> EmbedQueryBatch(IReadOnlyList<string> queries);
}

/// <summary>Runs Qwen3-Embedding-0.6B through ONNX Runtime: tokenize, run the model, take the last real token of last_hidden_state, normalize it.</summary>
public sealed class Qwen3EmbeddingProvider : IEmbeddingProvider, IDisposable
{
    public const int VectorSize = 1024;

    private const string HiddenStateName = "last_hidden_state";
    private const string InputIdsName = "input_ids";
    private const string AttentionMaskName = "attention_mask";
    private const string PositionIdsName = "position_ids";
    private const string CachePrefix = "past_key_values";

    private const string QueryPrefix = "Instruct: Given a search query, retrieve relevant passages that answer the query\nQuery: ";

    /// <summary>The pad token of Qwen3, the same id as the end of text token. Padding sits on the right, so a causal model never mixes it into a real token.</summary>
    private const long PadToken = 151643;

    private readonly InferenceSession _session;
    private readonly Tokenizer _tokenizer;
    private readonly string _outputName;
    private readonly bool _hasPositionIds;
    private readonly int _batchSize;

    public Qwen3EmbeddingProvider(Tokenizer tokenizer, IOptions<EmbeddingOptions> options, ILogger<Qwen3EmbeddingProvider> logger)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Value.BatchSize, nameof(EmbeddingOptions.BatchSize));

        string modelPath = Find(options.Value.ModelFile);
        _session = new InferenceSession(modelPath);
        _tokenizer = tokenizer;
        _outputName = _session.OutputMetadata.ContainsKey(HiddenStateName) ? HiddenStateName : _session.OutputMetadata.Keys.First();
        _hasPositionIds = _session.InputMetadata.ContainsKey(PositionIdsName);
        _batchSize = options.Value.BatchSize;

        int[] shape = _session.OutputMetadata[_outputName].Dimensions;
        if (shape.Length > 0 && shape[^1] > 0 && shape[^1] != VectorSize)
            throw new InvalidOperationException($"{modelPath} returns {shape[^1]} numbers per token, this provider reads {VectorSize}.");

        logger.LogInformation("Loaded {Model}: inputs {Inputs}, output {Output}, {Dimensions} dimensions, batch {BatchSize}", modelPath, string.Join(", ", _session.InputMetadata.Keys), _outputName, VectorSize, _batchSize);
    }

    /// <summary>Loads the tokenizer of this model. The document tree counts tokens with the same instance the provider uses.</summary>
    public static Tokenizer CreateTokenizer(EmbeddingOptions options) => new(vocabPath: Find(options.TokenizerFile));

    /// <summary>Looks for a file of the model up the directory tree, starting at the working directory and at the directory of the binary.</summary>
    public static string Find(string relativePath)
    {
        string[] roots = [Environment.CurrentDirectory, AppContext.BaseDirectory];
        foreach (string root in roots)
        {
            DirectoryInfo? directory = new(root);
            while (directory is not null)
            {
                string candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate))
                    return candidate;

                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"Model file not found: {relativePath}", relativePath);
    }

    public int Dimensions => VectorSize;

    public float[] EmbedDocument(string text) => Embed([text])[0];

    public float[] EmbedQuery(string query) => Embed([Prefix(query)])[0];

    public ImmutableArray<float[]> EmbedDocumentBatch(IReadOnlyList<string> texts) => EmbedSorted(texts, _batchSize);

    public ImmutableArray<float[]> EmbedQueryBatch(IReadOnlyList<string> queries) => EmbedSorted([.. queries.Select(Prefix)], _batchSize);

    private static string Prefix(string query) => $"{QueryPrefix}{query}";

    private ImmutableArray<float[]> Embed(IReadOnlyList<string> texts) => Run([.. texts.Select(_tokenizer.Encode)]);

    /// <summary>Sorts the texts by token count, runs them in batches of that order and puts every vector back in its own place.</summary>
    private ImmutableArray<float[]> EmbedSorted(IReadOnlyList<string> texts, int batchSize)
    {
        uint[][] tokens = [.. texts.Select(_tokenizer.Encode)];
        int[] order = [.. Enumerable.Range(0, tokens.Length).OrderBy(index => tokens[index].Length)];
        float[][] vectors = new float[tokens.Length][];

        for (int start = 0; start < order.Length; start += batchSize)
        {
            int[] batch = order[start..Math.Min(start + batchSize, order.Length)];
            ImmutableArray<float[]> part = Run([.. batch.Select(index => tokens[index])]);

            for (int row = 0; row < batch.Length; row++)
                vectors[batch[row]] = part[row];
        }

        return [.. vectors];
    }

    /// <summary>Runs one batch through the model. The tokenizer appends the end of text token, so the last real token of a row pools that row.</summary>
    private ImmutableArray<float[]> Run(IReadOnlyList<uint[]> tokens)
    {
        if (tokens.Any(row => row.Length == 0))
            throw new ArgumentException("A text without tokens has no last token to read.", nameof(tokens));

        int width = tokens.Max(row => row.Length);

        DenseTensor<long> ids = new([tokens.Count, width]);
        DenseTensor<long> mask = new([tokens.Count, width]);
        for (int row = 0; row < tokens.Count; row++)
        {
            for (int column = 0; column < width; column++)
            {
                bool real = column < tokens[row].Length;
                ids[row, column] = real ? tokens[row][column] : PadToken;
                mask[row, column] = real ? 1L : 0L;
            }
        }

        List<NamedOnnxValue> inputs = new(4)
        {
            NamedOnnxValue.CreateFromTensor(InputIdsName, ids),
            NamedOnnxValue.CreateFromTensor(AttentionMaskName, mask),
        };

        if (_hasPositionIds)
        {
            DenseTensor<long> positions = new([tokens.Count, width]);
            for (int row = 0; row < tokens.Count; row++)
            {
                for (int column = 0; column < width; column++)
                    positions[row, column] = column;
            }

            inputs.Add(NamedOnnxValue.CreateFromTensor(PositionIdsName, positions));
        }

        foreach (KeyValuePair<string, NodeMetadata> input in _session.InputMetadata)
        {
            if (!input.Key.StartsWith(CachePrefix, StringComparison.Ordinal))
                continue;

            int[] shape = [.. input.Value.Dimensions.Select(size => size < 0 ? 0 : size)];
            shape[0] = tokens.Count;
            inputs.Add(NamedOnnxValue.CreateFromTensor(input.Key, new DenseTensor<float>(shape)));
        }

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _session.Run(inputs, [_outputName]);
        Tensor<float> hidden = outputs.First().AsTensor<float>();

        // An export with dynamic shapes states nothing about its width, so the first real run is the place to find a mismatch.
        if (hidden.Dimensions[^1] != VectorSize)
            throw new InvalidOperationException($"The model returned {hidden.Dimensions[^1]} numbers per token, this provider reads {VectorSize}.");

        ImmutableArray<float[]>.Builder vectors = ImmutableArray.CreateBuilder<float[]>(tokens.Count);
        for (int row = 0; row < tokens.Count; row++)
            vectors.Add(Normalize(hidden, row, tokens[row].Length));

        return vectors.MoveToImmutable();
    }

    private static float[] Normalize(Tensor<float> hidden, int row, int length)
    {
        float[] vector = new float[VectorSize];
        for (int i = 0; i < VectorSize; i++)
            vector[i] = hidden[row, length - 1, i];

        float norm = 0f;
        foreach (float value in vector)
            norm += value * value;

        norm = MathF.Sqrt(norm);
        if (norm <= float.Epsilon)
            return vector;

        for (int i = 0; i < vector.Length; i++)
            vector[i] /= norm;

        return vector;
    }

    public void Dispose() => _session.Dispose();
}
