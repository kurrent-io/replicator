# Kurrent Replicator

Kurrent Replicator allows live copying events from one EventStoreDB and KurrentDB instance or cluster, to another.

Additional features:
- Filter out (drop) events
- Transform events
- Propagate streams metadata
- Propagate streams deletion

Implemented readers and writers:
- KurrentDB
- EventStoreDB gRPC (v20+)
- EventStore TCP (v4+)

## Build

```sh
docker build .
```

The default target architecture is amd64 (x86_64).

You can build targeting arm64 (e.g. to execute on Apple Silicon) like so:

```sh
docker build --build-arg RUNTIME=linux-arm64 .
```

## Documentation

Find out the details, including deployment scenarios, in the [documentation](https://replicator.kurrent.io).

## Support

Kurrent Replicator is provided as-is, without any warranty, and is not covered by Kurrent support contract.

If you experience an issue when using Replicator, or you'd like to suggest a new feature, please open an issue in this GitHub project.
