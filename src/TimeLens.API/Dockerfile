FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY ["src/TimeLens.API/TimeLens.API.csproj", "src/TimeLens.API/"]
COPY ["src/TimeLens.Domain/TimeLens.Domain.csproj", "src/TimeLens.Domain/"]
COPY ["src/TimeLens.Infrastructure/TimeLens.Infrastructure.csproj", "src/TimeLens.Infrastructure/"]
RUN dotnet restore "src/TimeLens.API/TimeLens.API.csproj"
COPY . .
RUN dotnet publish "src/TimeLens.API/TimeLens.API.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "TimeLens.API.dll"]
