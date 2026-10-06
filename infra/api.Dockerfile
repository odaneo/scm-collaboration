FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /source
ARG SERVICE
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet restore src/${SERVICE}.Api/${SERVICE}.Api.csproj --locked-mode
RUN dotnet publish src/${SERVICE}.Api/${SERVICE}.Api.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4
ARG SERVICE
ENV SCM_SERVICE=${SERVICE}
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
ENTRYPOINT ["sh", "-c", "exec dotnet /app/$SCM_SERVICE.Api.dll \"$@\"", "--"]
