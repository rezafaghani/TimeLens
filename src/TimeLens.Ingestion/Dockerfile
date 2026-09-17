FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/TimeLens.Ingestion/TimeLens.Ingestion.csproj", "src/TimeLens.Ingestion/"]
COPY ["src/TimeLens.Domain/TimeLens.Domain.csproj", "src/TimeLens.Domain/"]
COPY ["src/TimeLens.Infrastructure/TimeLens.Infrastructure.csproj", "src/TimeLens.Infrastructure/"]
RUN dotnet restore "src/TimeLens.Ingestion/TimeLens.Ingestion.csproj"
COPY . .
RUN dotnet publish "src/TimeLens.Ingestion/TimeLens.Ingestion.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TimeLens.Ingestion.dll"]
