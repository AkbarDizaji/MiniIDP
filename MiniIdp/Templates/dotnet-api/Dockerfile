# Stage 1: build with the full .NET SDK.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, in its own layer, so it's cached when only code changes.
COPY ServiceApi.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish --configuration Release --no-restore --output /app

# Stage 2: run on the much smaller ASP.NET runtime image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Listen on 8080 inside the container; Mini IDP maps it to a free port on 127.0.0.1.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Run as the non-root user that the official .NET images provide.
USER $APP_UID
ENTRYPOINT ["dotnet", "ServiceApi.dll"]
