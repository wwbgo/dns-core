# DnsCore Benchmarks

基于 BenchmarkDotNet 的 DNS 热路径微基准。

## 运行

```bash
# 运行全部基准，使用较短任务，便于快速回归
dotnet run -c Release --project benchmarks/DnsCore.Benchmarks/DnsCore.Benchmarks.csproj -- --job short --filter "*DnsHotPathBenchmarks*"

# 只运行缓存命中
dotnet run -c Release --project benchmarks/DnsCore.Benchmarks/DnsCore.Benchmarks.csproj -- --job short --filter "*CacheHit*"
```

## 覆盖项

- DNS 查询解析
- DNS 响应构建
- 缓存命中与未命中
- 自定义记录精确匹配
- 自定义记录泛域名匹配
- ACL 来源网段判断

结果会输出到 `BenchmarkDotNet.Artifacts/results/`，该目录已加入 `.gitignore`。
