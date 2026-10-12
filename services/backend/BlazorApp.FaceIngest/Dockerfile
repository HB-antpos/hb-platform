# =============================================================================
# 人脸抓拍接收服务（BlazorApp.FaceIngest，P0 接收落盘 + P1 后台处理）Docker 配置
# 独立于主后端 hb-platform-vite-api（5002）与收银后端 hbpos-api（5003），容器固定使用 5004 端口。
# 构建上下文是 services/backend（与 hb-api 一致）；生产用同目录的 docker-compose.face-ingest.yml
# 构建与启动，步骤见 docs/runbooks/face-ingest.md。本地单独验证镜像：
#   docker build -f services/backend/BlazorApp.FaceIngest/Dockerfile services/backend
# =============================================================================

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# 清理 NuGet 本地缓存，保持容器构建和当前源代码依赖一致。
RUN dotnet nuget locals all --clear

# 先只复制项目文件，利用 Docker layer cache 加速 restore。
# 不引用 BlazorApp.Shared / BlazorApp.Api（P1 只多了 Microsoft.Data.SqlClient 包）；不要把测试项目带进镜像构建。
COPY BlazorApp.FaceIngest/BlazorApp.FaceIngest.csproj ./BlazorApp.FaceIngest/

RUN dotnet restore "BlazorApp.FaceIngest/BlazorApp.FaceIngest.csproj" --verbosity minimal

COPY BlazorApp.FaceIngest/ ./BlazorApp.FaceIngest/

# 本机直接 docker build 时，构建上下文里的 obj/ 会盖掉容器内的 restore 结果（services/backend
# 的 .gitignore 忽略了 .dockerignore，无法随仓库提交），所以 --no-restore 失败时回退完整 restore。
WORKDIR /src/BlazorApp.FaceIngest
RUN dotnet build "BlazorApp.FaceIngest.csproj" -c Release -o /app/build --no-restore || \
    (echo 'Retrying build with restore...' && \
     dotnet restore "BlazorApp.FaceIngest.csproj" --verbosity minimal && \
     dotnet build "BlazorApp.FaceIngest.csproj" -c Release -o /app/build --no-restore)

FROM build AS publish
RUN dotnet publish "BlazorApp.FaceIngest.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# healthcheck 依赖 curl。
# appuser 固定 uid 1000：宿主机存储目录与配置目录按 1000:1000、权限 700 创建（见 runbook），
# uid 对不上时容器既写不进原始报文、也读不到 cameras.json。
# /app/App_Data/FaceIngest 与 /app/config 是挂载点，预先建好并交给 appuser，未挂载时也能直接调试。
# 关键逻辑：只 chown 这两个挂载点，/app 本身保持 root 所有。若把 /app 交给 appuser，
# appuser 虽改不了 root 所有的 DLL，却能在自己可写的目录里把它删除 / 改名后换成别的文件
# （进程被利用后可借此长期驻留，restart 复用同一容器可写层，直到容器重建）。
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl && \
    rm -rf /var/lib/apt/lists/* && \
    adduser --disabled-password --gecos "" --uid 1000 appuser && \
    mkdir -p /app/App_Data/FaceIngest /app/config && \
    chown -R appuser /app/App_Data /app/config

# 程序文件与 /app 目录都保持 root 所有，appuser 只读执行；运行期只写 /app/App_Data/FaceIngest。
COPY --from=publish /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_HTTP_PORTS=5004

USER appuser

HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 \
    CMD curl -f http://localhost:5004/health || exit 1

EXPOSE 5004

ENTRYPOINT ["dotnet", "BlazorApp.FaceIngest.dll"]
