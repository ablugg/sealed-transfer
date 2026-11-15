# --- Stage 1: Build the Angular frontend ---
FROM node:22-slim AS web-build
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npx ng build --configuration production

# --- Stage 2: Build the .NET API ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src
COPY SealedTransfer.slnx ./
COPY src/SealedTransfer.Core/SealedTransfer.Core.csproj src/SealedTransfer.Core/
COPY src/SealedTransfer.Infrastructure/SealedTransfer.Infrastructure.csproj src/SealedTransfer.Infrastructure/
COPY src/SealedTransfer.Api/SealedTransfer.Api.csproj src/SealedTransfer.Api/
RUN dotnet restore src/SealedTransfer.Api/SealedTransfer.Api.csproj
COPY src/ src/
RUN dotnet publish src/SealedTransfer.Api/SealedTransfer.Api.csproj -c Release -o /app --no-restore

# --- Stage 3: Runtime image ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=api-build /app ./
# Copy Angular production build into wwwroot so the API serves it.
COPY --from=web-build /web/dist/web/browser wwwroot/

ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "SealedTransfer.Api.dll"]
