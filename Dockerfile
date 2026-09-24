FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/SmtOrders.Api/SmtOrders.Api.csproj src/SmtOrders.Api/
RUN dotnet restore src/SmtOrders.Api/SmtOrders.Api.csproj
COPY src/SmtOrders.Api/ src/SmtOrders.Api/
RUN dotnet publish src/SmtOrders.Api/SmtOrders.Api.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SmtOrders.Api.dll"]
