#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["BellfiresMQTTServer/BellfiresMQTTServer.csproj", "BellfiresMQTTServer/"]
RUN dotnet restore "BellfiresMQTTServer/BellfiresMQTTServer.csproj"
COPY . .
WORKDIR "/src/BellfiresMQTTServer"
RUN dotnet build "BellfiresMQTTServer.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "BellfiresMQTTServer.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "BellfiresMQTTServer.dll"]