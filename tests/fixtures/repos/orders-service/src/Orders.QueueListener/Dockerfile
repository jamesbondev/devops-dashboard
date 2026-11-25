FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Orders.QueueListener/Orders.QueueListener.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:0f2b7c3c1b5e0a5d7f8e9a1b2c3d4e5f60718293a4b5c6d7e8f9a0b1c2d3e4f5
WORKDIR /app
COPY --from=build /app .
USER app
ENTRYPOINT ["dotnet", "Orders.QueueListener.dll"]
