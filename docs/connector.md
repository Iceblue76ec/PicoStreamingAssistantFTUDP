# Legacy UDP reception

The production listener binds IPv4 `0.0.0.0:29765`. `Connect()` returns after a
successful bind and starts an asynchronous receiver; it does not wait for a first
sample or prove that the headset is sending data. Bind failure closes local resources
and returns false. The module retries discovery/configuration/binding 5 seconds after
the failed attempt ends, clearing that deadline on success.

## Packet layout and validation

| Datagram offset | Bytes | Field | Used by tracking |
| --- | ---: | --- | --- |
| 0..15 | 16 | `TrackingDataHeader` | Only `tracking_type` at offset 2 |
| 16..23 | 8 | `PxrFTInfo.timestamp` | Skipped |
| 24..311 | 288 | 72 `float` weights | Copied to the latest sample |
| 312..351 | 40 | `videoInputValid` | Ignored |
| 352..355 | 4 | `laughingProb` | Ignored |
| 356..395 | 40 | `emotionProb` | Ignored |
| 396..907 | 512 | `reserved` | Ignored |

The full header and payload occupy 908 bytes. The receiver requires only the consumed
prefix: `16 + 8 + 72 × 4 = 312` bytes, and `tracking_type == 2`. This deliberately permits
missing/extra trailing fields; it does not establish that the protocol defines them as
optional. Parsing uses native `float` layout, matching the little-endian Windows sender
and supported runtime. Neither timestamp is read. Start codes, version and individual
weight values are not validated; passing length/type checks alone does not authenticate
or fully validate a packet. Short packets and other types are skipped without clearing
a previous valid sample or stopping the receiver.

`multi_packet` and `current_packet_index` are not interpreted. There is no cross-datagram
fragment assembly: one datagram must contain all 72 weights. Frames fragmented at the
application level are not supported.

## Latest-sample handoff

Each connection owns a fixed receive buffer. A batch handles up to 1024 queued datagrams
and publishes its newest valid sample; at the cap the receiver yields and checks
cancellation before the next batch. An invalid trailing packet cannot discard the valid
sample selected earlier in the batch.

One sample slot and a short-held lock protect frame publication. The single Update
consumer copies 72 weights into its own buffer under that lock. The returned
`ReadOnlySpan<float>` remains stable until the next `GetBlendShapes()` call. Mapping,
logging, process probes and task waits run outside the sample lock.

Without a sample, Update waits for notification for at most 100ms. A new sample, receiver
failure or teardown wakes it early. Background reception continues while the module is
paused or processing backs off; pending samples are replaced, not accumulated.

An unexpected receiver termination preserves its original exception and logs the first
detail. `GetBlendShapes()` throws internal `ReceiveLoopException`; Update backs off for
1 second and rebuilds the receiver. Processing failures also back off for 1 second but
retain the connection. Missing data, rejected packets and handled receive timeouts do
not trigger either error backoff.

Teardown marks the session closed and wakes readers, then cancels/closes the socket. It
waits at most 1 second for the receive Task, warning on timeout. The receive loop's
`finally` completes resource cleanup; expected shutdown cancellation is silent. Repeated
teardown is safe, and an old session cannot publish into a replacement session.

## Protocol and hybrid boundaries

PICO Connect and Business Streaming 2.x require `faceTrackingTransferProtocol=2` for
this decoder. Business Streaming 1.x and Streaming Assistant retain the same legacy
path. Unsupported configurable protocols fail connection with a bounded warning.
Streamer priority and upstream configuration handling are preserved.

`faceTrackingMode` is not read. Hybrid mode may zero mouth blendshapes during speech;
visemes are not combined with mouth weights. The upstream hybrid test remains ignored.
See [PICO configuration](https://docs.vrcft.io/docs/hardware/vr/pico/pico4pe) and
[diagnostic logging](logging.md).
