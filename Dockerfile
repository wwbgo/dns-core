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

# 构建脚本与 compose 都会传入这两个参数，此前 Dockerfile 未声明，
# 导致构建时提示 "build args were not consumed"。这里声明并写入 OCI 标签。
ARG BUILD_DATE=unknown
ARG VERSION=latest

LABEL org.opencontainers.image.title="DNS Core Server" \
      org.opencontainers.image.description="高性能 DNS 服务器，支持自定义记录、上游转发与 Web 管理" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.created="${BUILD_DATE}" \
      org.opencontainers.image.licenses="MIT"

# 安装运行期依赖：
#   curl        —— HEALTHCHECK 需要。aspnet:10.0 基础镜像不含 curl，
#                  缺失时健康检查恒定失败，容器一直是 unhealthy。
#   libcap2-bin —— 提供 setcap，用于授予绑定 53 端口的能力（见下）。
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl libcap2-bin \
    && rm -rf /var/lib/apt/lists/*

# 创建非 root 用户（安全最佳实践）
RUN groupadd -r dnscore && useradd -r -g dnscore dnscore

# 授予 dotnet 绑定特权端口的能力。
# 容器以非 root 用户运行，而 DNS 需要监听 53（<1024）。
# CAP_NET_BIND_SERVICE 虽在默认 bounding set 内，但非 root 进程的
# effective set 为空，必须通过文件能力提升，否则 bind 会抛
# SocketException(13) Permission denied。
# 注意要作用于真实二进制（/usr/bin/dotnet 只是符号链接）。
RUN setcap 'cap_net_bind_service=+ep' "$(readlink -f "$(command -v dotnet)")"

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
