using Kurrent.Replicator.Shared;
using Kurrent.Replicator.Shared.Logging;

namespace Kurrent.Replicator;

public class ChaserCheckpointSeeder(string filePath, ICheckpointStore checkpointStore) : ICheckpointSeeder {
    static readonly ILog Log = LogProvider.GetCurrentClassLogger();

    public async ValueTask Seed(CancellationToken cancellationToken) {
        if (await checkpointStore.HasStoredCheckpoint(cancellationToken)) {
            Log.Info("Checkpoint already present in store, skipping seeding");

            return;
        }

        if (!File.Exists(filePath)) {
            Log.Warn("Seeding failed because the file at {FilePath} does not exist", filePath);

            return;
        }

        await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        if (fileStream.Length != 8) {
            Log.Warn("Seeding failed because the file at {FilePath} does not appear to be an 8-byte position file", filePath);

            return;
        }

        using var reader   = new BinaryReader(fileStream);
        var       position = reader.ReadInt64();
        await checkpointStore.StoreCheckpoint(new LogPosition(0L, (ulong)position), cancellationToken);
    }
}
