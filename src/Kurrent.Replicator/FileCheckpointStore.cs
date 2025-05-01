using Kurrent.Replicator.Shared;
using Kurrent.Replicator.Shared.Logging;

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
            Log.Info("Starting from a previously known checkpoint {LastKnown}", _lastPosition);

            return _lastPosition;
        }

        if (!File.Exists(_fileName)) {
            Log.Info("No checkpoint file found, starting from the beginning");

            return LogPosition.Start;
        }

        var content = await File.ReadAllTextAsync(_fileName, cancellationToken).ConfigureAwait(false);
        var numbers = content.Split(',').Select(x => Convert.ToInt64(x)).ToArray();

        Log.Info("Loaded the checkpoint from file: {Checkpoint}", numbers[1]);

        return new LogPosition(numbers[0], (ulong)numbers[1]);
    }

    int          _counter;
    LogPosition? _lastPosition;

    public async ValueTask StoreCheckpoint(LogPosition logPosition, CancellationToken cancellationToken) {
        _lastPosition = logPosition;

        Interlocked.Increment(ref _counter);

        if (_counter < _checkpointAfter) return;

        await Flush(cancellationToken).ConfigureAwait(false);

        Interlocked.Exchange(ref _counter, 0);
    }

    public async ValueTask Flush(CancellationToken cancellationToken) {
        if (_lastPosition == null) return;

        await File.WriteAllTextAsync(_fileName, $"{_lastPosition.EventNumber},{_lastPosition.EventPosition}", cancellationToken).ConfigureAwait(false);
    }
}
