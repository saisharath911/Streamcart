# syntax=docker/dockerfile:1.7
# One Dockerfile for every service: docker build --build-arg SERVICE=Orders -f src/Services/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG SERVICE
WORKDIR /src

# Restore first (cached until a project file or package version changes).
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/StreamCart.BuildingBlocks/StreamCart.BuildingBlocks.csproj src/BuildingBlocks/StreamCart.BuildingBlocks/
COPY src/Services/${SERVICE}/StreamCart.${SERVICE}.Api/StreamCart.${SERVICE}.Api.csproj src/Services/${SERVICE}/StreamCart.${SERVICE}.Api/
RUN dotnet restore src/Services/${SERVICE}/StreamCart.${SERVICE}.Api/StreamCart.${SERVICE}.Api.csproj

COPY src/ src/
RUN dotnet publish src/Services/${SERVICE}/StreamCart.${SERVICE}.Api/StreamCart.${SERVICE}.Api.csproj \
    -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG SERVICE
ENV ASPNETCORE_HTTP_PORTS=8080 \
    SERVICE_DLL=StreamCart.${SERVICE}.Api.dll
WORKDIR /app
COPY --from=build /app .
# Non-root user provided by the official .NET images.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$SERVICE_DLL\""]
