# DNS Core Server Dockerfile
# 多阶段构建以减小镜像体积

# ===== 构建阶段 =====
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 复制解决方案文件和项目文件
# COPY ["DnsCore.sln", "./"]
COPY ["src/DnsCore/DnsCore.csproj", "src/DnsCore/"]

# 还原 NuGet 包（包含 Microsoft.Data.Sqlite 和 LiteDB）
RUN dotnet restore "src/DnsCore/DnsCore.csproj"

# 复制所有源代码
COPY ["src/", "src/"]

# 构建项目
WORKDIR "/src/src/DnsCore"
RUN dotnet build "DnsCore.csproj" -c Release -o /app/build

# ===== 发布阶段 =====
FROM build AS publish
RUN dotnet publish "DnsCore.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ===== 运行阶段 =====
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# 创建非 root 用户（安全最佳实践）
RUN groupadd -r dnscore && useradd -r -g dnscore dnscore

# 创建数据目录用于持久化存储
RUN mkdir -p /app/data && chown -R dnscore:dnscore /app/data

# 复制发布的文件
COPY --from=publish /app/publish .

# 设置文件权限
RUN chown -R dnscore:dnscore /app

# 创建数据卷
VOLUME ["/app/data"]

# 暴露端口
# 53/UDP - DNS 服务端口（UDP）
# 53/TCP - DNS 服务端口（TCP）
# 5000/TCP - HTTP Web 管理界面和 API
EXPOSE 53/udp
EXPOSE 53/tcp
EXPOSE 5000

# 切换到非 root 用户
# 注意：DNS 默认需要 53 端口，可能需要特权或端口重映射
USER dnscore

# 设置环境变量
ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_EnableDiagnostics=0

# 设置 UTF-8 编码支持（修复中文乱码）
ENV LANG=zh_CN.UTF-8
ENV LC_ALL=zh_CN.UTF-8
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# 健康检查
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD curl --fail http://localhost:5000/health || exit 1

# 启动应用
ENTRYPOINT ["dotnet", "DnsCore.dll"]
