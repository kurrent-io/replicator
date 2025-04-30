---
title: "Scavenge"
sidebar:
    order: 1
description: >
    Filter out events from deleted streams, expired by age or count, but not yet scavenged.
---

By default, Replicator would watch for events, which should not be present in the source cluster as they are considered as deleted, but not removed from the database due to lack of scavenge.

Example cases:
- The database wasn't scavenged for a while
- A stream was deleted, but not scavenged yet
- A stream was truncated either by max age or max count, but not scavenged yet

All those events will not be replicated.

This feature can be disabled by setting the `replicator.scavenge` option to `false`.
