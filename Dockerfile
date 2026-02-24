# Stage 1: Build frontend
FROM node:22-alpine AS frontend
WORKDIR /src/AgarIA.Web
COPY AgarIA.Web/package.json AgarIA.Web/package-lock.json* ./
RUN npm ci
COPY AgarIA.Web/ ./
RUN npm run build

# Stage 2: Build .NET application
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY AgarIA.slnx ./
COPY AgarIA.Core.Data/AgarIA.Core.Data.csproj AgarIA.Core.Data/
COPY AgarIA.Core.Repositories/AgarIA.Core.Repositories.csproj AgarIA.Core.Repositories/
COPY AgarIA.Core.Game/AgarIA.Core.Game.csproj AgarIA.Core.Game/
COPY AgarIA.Core.AI/AgarIA.Core.AI.csproj AgarIA.Core.AI/
COPY AgarIA.Web/AgarIA.Web.csproj AgarIA.Web/
RUN dotnet restore AgarIA.slnx
COPY . .
COPY --from=frontend /src/AgarIA.Web/wwwroot/dist/ AgarIA.Web/wwwroot/dist/
RUN dotnet publish AgarIA.Web -c Release -o /app

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 5274
ENV ASPNETCORE_URLS=http://+:5274
ENTRYPOINT ["dotnet", "AgarIA.Web.dll"]
