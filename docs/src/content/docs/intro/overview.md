---
title: Overview
description: What is Replicator and why you might want to use it.
sidebar:
  order: 1
---

## What is it?

Event Store Replicator, as the name suggests, aims to address the need to keep one KurrentDB cluster in sync with another. It does so by establishing connections to both source and target clusters, then reading all the events from the source, and propagating them to the target. During the replication process, the tool can apply some rules, which allow you to exclude some events from being propagated to the target.

## Why would you use it?

Common replication scenarios include:

* **Cloud migration**: When migrating from a self-hosted cluster to Kurrent Cloud, you might want to take the data with you. Replicator can help you with that, but it has some limitations on how fast it can copy the historical data.

* **Store migration**: Migrating the whole store when your event schema changes severely, or you need to get rid of some obsolete data, you can do it using Replicator too. You can transform events from one contract to another, and filter out obsolete events. It allows you also to overcome the limitation of not being able to delete events in the middle of the stream. Greg Young promotes a complete store migration with transformation as part of the release cycle, to avoid event versioning issues. You can, for example, [listen about it here](https://youtu.be/FKFu78ZEIi8?t=856).

* **Backup**: You can also replicate data between two clusters, so in case of catastrophic failure, you will have a working cluster with recent data.

* **What is it *not yet* good for?**: Replicator uses client protocols (TCP and gRPC), with all the limitations. For example, to keep the global event order intact, you must use a single writer. As the transaction scope is limited to one stream, you get sequential writes of one event at a time, which doesn't deliver exceptional speed. Relaxing ordering guarantees helps to increase the performance, but for large databases (hundreds of millions events and more) and guaranteed global order, it might not be the tool for you.

