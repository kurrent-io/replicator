function route(stream, eventType, data, meta) {
    return {
        topic: "myTopic",
        partitionKey: stream
    }
}