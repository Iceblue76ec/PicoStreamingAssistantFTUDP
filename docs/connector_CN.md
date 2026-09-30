# Legacy UDP 收包

生产监听绑定 IPv4 `0.0.0.0:29765`。`Connect()` 在绑定成功后启动异步接收并返回，不等待首包，
也不证明头显已发送数据。绑定失败清理本地资源并返回 false。模块从服务探测、配置或绑定失败结束时起
退避 5 秒，连接成功清除该窗口。

## 报文布局与校验

| 数据报偏移 | 字节数 | 字段 | 追踪使用方式 |
| --- | ---: | --- | --- |
| 0..15 | 16 | `TrackingDataHeader` | 只检查偏移 2 的 `tracking_type` |
| 16..23 | 8 | `PxrFTInfo.timestamp` | 跳过 |
| 24..311 | 288 | 72 个 `float` 权重 | 复制到最新样本槽 |
| 312..351 | 40 | `videoInputValid` | 不读取 |
| 352..355 | 4 | `laughingProb` | 不读取 |
| 356..395 | 40 | `emotionProb` | 不读取 |
| 396..907 | 512 | `reserved` | 不读取 |

完整报文为 908 字节。当前接收端只要求使用到的前缀：`16 + 8 + 72 × 4 = 312` 字节，
且 `tracking_type == 2`。因此允许尾部缺省或额外后缀；这是接收端的兼容选择，不代表协议定义尾部可选。
解析使用原生 `float` 布局，与 little-endian Windows 发送端及支持的运行时一致。两个 timestamp 均不读取。
起始码、版本和各权重数值不校验；通过长度/类型检查不代表完整校验或来源认证。
短包和其他类型包会跳过，不清除已有有效样本，也不终止接收循环。

`multi_packet` 和 `current_packet_index` 不读取。不实现跨数据报分片重组；每个数据报必须包含全部
72 个权重，不支持应用层拆分的样本。

## 最新样本交接

每个连接复用固定接收 buffer。每批最多处理 1024 个排队报文，发布本批最新有效样本；达到上限后让出执行
机会，下批开始前检查取消。后续错误包不能覆盖本批已经选择的有效样本。

一份样本槽和短时持有的锁保护整帧交接。单个 Update 消费者在锁内复制 72 个权重到独立 buffer，
返回的 `ReadOnlySpan<float>` 直到下一次 `GetBlendShapes()` 才改变。映射、日志、进程探测和 Task 等待
均在样本锁外完成。

无样本时消费者通过通知等待，最长 100ms。新样本、接收故障或关闭提前唤醒。
模块暂停或处理退避期间后台继续接收，只替换最新待消费样本，不积累历史队列。

接收循环意外终止时保留原始异常并立即输出首次详情。`GetBlendShapes()` 抛出内部 `ReceiveLoopException`，
模块退避 1 秒后重建。处理异常同样退避 1 秒，但保留连接。无数据、拒收报文和已处理的接收超时不触发异常退避。

关闭先标记 session 已关闭并唤醒消费者，再取消和关闭 socket，最多等待接收 Task 退出 1 秒，超时记录 Warning。
接收循环的 `finally` 完成最终资源释放；主动取消不作为故障输出。重复关闭安全，旧 session 不会向新 session 发布样本。

## 协议与 hybrid 边界

PICO Connect 和 Business Streaming 2.x 只支持 `faceTrackingTransferProtocol=2`。
Business Streaming 1.x 和 Streaming Assistant 保持 legacy 路径。不支持的可配置协议会返回连接失败并限制提示频率。
保留上游的程序优先级及配置处理方式。

不读取 `faceTrackingMode`。Hybrid 模式可能在说话时将嘴部 blendshape 清零，模块不组合 viseme 和嘴部权重。
上游 hybrid 测试保留 Ignore。参见 [PICO 配置](https://docs.vrcft.io/docs/hardware/vr/pico/pico4pe) 及
[诊断日志](logging_CN.md)。
