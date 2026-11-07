using System.Collections.Concurrent;

using Microsoft.Extensions.Options;

using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Code;

/// <summary>
/// Reads the code graph of a code base with Roslyn and MSBuild.
/// </summary>
/// <remarks>
/// After a full build the extractor keeps the loaded workspace for a while, so <see cref="UpdateAsync"/> can read the graph of a few files again.
/// </remarks>
/// <param name="options">The options.</param>
/// <param name="timeProvider">The clock of the expiry of kept workspaces, or <see langword="null"/> for the system clock.</param>
public sealed class RoslynCodeGraphExtractor(IOptions<CodeGraphOptions> options, TimeProvider? timeProvider = null) : IIncrementalCodeGraphExtractor, IDisposable
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, KeptWorkspace> _kept = new(KeptWorkspace.KeyComparer);

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public async Task<CodeGraph> ExtractAsync(CodeGraphInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        CodeGraphOptions settings = options.Value;
        string sdkVersion = MsBuildRegistration.Register(settings.MsBuildPath);
        Release(input.RootPath);

        (CodeGraph graph, KeptWorkspace? entry) = await new GraphBuilder(input, settings, sdkVersion).BuildAsync(settings.KeepWorkspaceMinutes > 0, cancellationToken).ConfigureAwait(false);
        if (entry is not null)
            Keep(entry, TimeSpan.FromMinutes(settings.KeepWorkspaceMinutes));

        return graph;
    }

    /// <inheritdoc/>
    public async Task<CodeGraphPatch?> UpdateAsync(CodeGraphUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        string key = KeptWorkspace.KeyOf(update.Input.RootPath);
        if (!_kept.TryGetValue(key, out KeptWorkspace? entry) || !entry.Matches(update.Input))
            return null;

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (entry.IsClosed)
                return null;

            Touch(entry);
            return await new GraphUpdater(entry, options.Value).UpdateAsync(update, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Touch(entry);
            entry.ReleaseGate();
        }
    }

    /// <inheritdoc/>
    public void Release(string rootPath)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        if (_kept.TryRemove(KeptWorkspace.KeyOf(rootPath), out KeptWorkspace? entry))
            entry.Retire();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (string key in _kept.Keys)
        {
            if (_kept.TryRemove(key, out KeptWorkspace? entry))
                entry.Retire();
        }
    }

    private void Keep(KeptWorkspace entry, TimeSpan lifetime)
    {
        entry.Timer = _time.CreateTimer(_ => Expire(entry), null, lifetime, Timeout.InfiniteTimeSpan);
        KeptWorkspace? previous = null;
        _kept.AddOrUpdate(
            entry.Key,
            entry,
            (_, existing) =>
            {
                previous = existing;
                return entry;
            });
        previous?.Retire();
    }

    private void Touch(KeptWorkspace entry)
    {
        TimeSpan lifetime = TimeSpan.FromMinutes(options.Value.KeepWorkspaceMinutes);
        if (lifetime > TimeSpan.Zero)
            entry.Timer?.Change(lifetime, Timeout.InfiniteTimeSpan);
    }

    private void Expire(KeptWorkspace entry)
    {
        _kept.TryRemove(new KeyValuePair<string, KeptWorkspace>(entry.Key, entry));
        entry.Retire();
    }
}
