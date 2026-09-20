# Size and memory, measured

`akuwm.exe`, self-contained `win-x64`, single file, on DESK_W11:

| build | disk | start | working set | private RAM |
|---|---|---|---|---|
| **plain (what ships)** | 89.6 MB | 293 ms | 47.5 MB | **11.8 MB** |
| `EnableCompressionInSingleFile` | 39.9 MB | 351 ms | 75.7 MB | 29.8 MB |
| `PublishReadyToRun` | 142.1 MB | 306 ms | 58.0 MB | 22.0 MB |

Plain wins the two that matter for a process that runs all day.

Most of the plain build's working set is the **mapped image**: shared,
file-backed, and the OS can evict it under pressure. Compression cannot be
shared — the assemblies are decompressed into private memory that can only be
paged out — so it trades 50 MB of disk, which is free, for 18 MB of resident
RAM, which is not. ReadyToRun buys no startup here (the bundle already maps
the IL) and costs both.

## What would actually improve both

Trimming. It is blocked by ten reflection sites, all of them JSON:

| where | why |
|---|---|
| `Config/ConfigJson.cs:36,39` | `JsonSerializer.Serialize/Deserialize<AkuWmConfig>` |
| `Ipc/Protocol.cs:52,62,65` | command responses are **anonymous types** |
| `State/AtomicJson.cs:43,76` | the session marker |
| `Config/ConfigMerge.cs:138,149,152` | reflects over config properties to merge by id |

The work: a `JsonSerializerContext` for `AkuWmConfig` and `Session`, replacing
every anonymous type in a command response with a `JsonObject`, and
`[DynamicallyAccessedMembers]` on `ConfigMerge`'s type parameter. That is a
real project and it touches the shape of every command reply — the thing the
scripts parse. Worth doing when the command surface stops moving; not before.

`JsonArray.Add<T>` was a third of the blockers and is gone: the generic
overload builds a node that cannot serialise without a serializer context,
which is also a **runtime** trap — it throws the moment the bar asks for the
desk and nowhere earlier. Every call site casts to `(JsonNode)`.
