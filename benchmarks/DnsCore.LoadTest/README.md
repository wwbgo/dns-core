# DnsCore UDP Load Test

本地 UDP DNS 负载测试工具，不依赖外部压测程序。

## 运行

```bash
dotnet run -c Release --project benchmarks/DnsCore.LoadTest/DnsCore.LoadTest.csproj -- 8
dotnet run -c Release --project benchmarks/DnsCore.LoadTest/DnsCore.LoadTest.csproj -- 32
```

参数为并发 worker 数。工具会在 `127.0.0.1:15399` 启动一个临时 DNS 服务，使用本地自定义记录应答，并输出 QPS、错误数、P50/P95/P99。

当前结果仅反映本机开发环境，适合做相对回归，不应直接等同于生产吞吐。
