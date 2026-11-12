FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY ["src/TimeLens.Insert/TimeLens.Insert.csproj", "src/TimeLens.Insert/"]
COPY ["src/TimeLens.Domain/TimeLens.Domain.csproj", "src/TimeLens.Domain/"]
COPY ["src/TimeLens.Infrastructure/TimeLens.Infrastructure.csproj", "src/TimeLens.Infrastructure/"]
RUN dotnet restore "src/TimeLens.Insert/TimeLens.Insert.csproj"
COPY . .
RUN dotnet publish "src/TimeLens.Insert/TimeLens.Insert.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TimeLens.Insert.dll"]
