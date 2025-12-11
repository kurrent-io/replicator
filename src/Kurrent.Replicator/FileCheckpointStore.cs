using Kurrent.Replicator.Shared;
using Kurrent.Replicator.Shared.Logging;
using Kurrent.Replicator.Shared.Observe;

namespace Kurrent.Replicator;

public class FileCheckpointStore : ICheckpointStore {
    static readonly ILog Log = LogProvider.GetCurrentClassLogger();

    readonly string _fileName;
    readonly int    _checkpointAfter;

    public FileCheckpointStore(string filePath, int checkpointAfter) {
        _fileName        = filePath;
        _checkpointAfter = checkpointAfter;

        try {
            if (File.Exists(filePath)) {
                return;
            }

            File.AppendAllText(filePath, "test");
            File.Delete(filePath);
        } catch (Exception e) {
            Log.Fatal(e, "Unable to write to {File}", filePath);

            throw;
        }
    }

    public ValueTask<bool> HasStoredCheckpoint(CancellationToken cancellationToken) {
        return ValueTask.FromResult(File.Exists(_fileName) && new FileInfo(_fileName).Length > 0);
    }

    public async ValueTask<LogPosition> LoadCheckpoint(CancellationToken cancellationToken) {
        if (_lastPosition != null) {
            Log.Info(
                "[Run {RunId}] Loading checkpoint {Checkpoint}. Source={Source}",
                ReplicationRun.RunId,
                _lastPosition,
                "InMemory"
            );

            return _lastPosition;
        }

        if (!File.Exists(_fileName)) {
            Log.Info(
                "[Run {RunId}] Loading checkpoint {Checkpoint}. Source={Source}",
                ReplicationRun.RunId,
                LogPosition.Start,
                "Default"
            );

            return LogPosition.Start;
        }

        var content = await File.ReadAllTextAsync(_fileName, cancellationToken).ConfigureAwait(false);
        var numbers = content.Split(',').Select(x => Convert.ToInt64(x)).ToArray();
        var checkpoint = new LogPosition(numbers[0], (ulong)numbers[1]);

        Log.Info(
            "[Run {RunId}] Loading checkpoint {Checkpoint}. Source={Source}",
            ReplicationRun.RunId,
            checkpoint,
            "File"
        );

        return checkpoint;
    }

    int          _counter;
    LogPosition? _lastPosition;

    public async ValueTask StoreCheckpoint(LogPosition logPosition, CancellationToken cancellationToken) {
        // Ensure checkpoint never moves backwards within a run.
        if (_lastPosition != null && logPosition.EventPosition < _lastPosition.EventPosition) {
            Log.Error(
                "Attempt to move checkpoint backwards. Current={Current}, New={New}",
                _lastPosition,
                logPosition
            );

            // Ignore the backwards update to maintain monotonicity.
            return;
        }

        _lastPosition = logPosition;

        Interlocked.Increment(ref _counter);

        if (_counter < _checkpointAfter) return;

        await Flush(cancellationToken).ConfigureAwait(false);

        Interlocked.Exchange(ref _counter, 0);
    }

    public async ValueTask Flush(CancellationToken cancellationToken) {
        if (_lastPosition == null) return;

        LogPosition? previousCheckpoint = null;

        if (File.Exists(_fileName)) {
            var existingContent = await File.ReadAllTextAsync(_fileName, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(existingContent)) {
                var parts = existingContent.Split(',').Select(x => Convert.ToInt64(x)).ToArray();

                if (parts.Length == 2)
                    previousCheckpoint = new LogPosition(parts[0], (ulong)parts[1]);
            }
        }

        Log.Debug(
            "Flushing checkpoint. Run={RunId}, Previous={PreviousCheckpoint}, New={NewCheckpoint}",
            ReplicationRun.RunId,
            previousCheckpoint,
            _lastPosition
        );

        await File.WriteAllTextAsync(
            _fileName,
            $"{_lastPosition.EventNumber},{_lastPosition.EventPosition}",
            cancellationToken
        ).ConfigureAwait(false);
    }
}
