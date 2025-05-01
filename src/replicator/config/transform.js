function transform(original) {
    log.debug("Transforming event {Type} from {Stream}", original.EventType, original.Stream);

    if (original.Stream.length > 7) return undefined;

    const newEvent = {
        ...original.Data,
        Data1: `new${original.Data.Data1}`,
        NewProp: `${original.Data.Id} - ${original.Data.Data2}`
    };
    return {
        Stream: `transformed${original.Stream}`,
        EventType: `V2.${original.EventType}`,
        Data: newEvent,
        Meta: original.Meta
    }
}