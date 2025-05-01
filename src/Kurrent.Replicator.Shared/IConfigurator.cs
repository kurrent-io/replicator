namespace Kurrent.Replicator.Shared; 

public interface IConfigurator {
    string            Protocol { get; }
    IEventReader      ConfigureReader(string connectionString);
    IEventWriter      ConfigureWriter(string connectionString);
}