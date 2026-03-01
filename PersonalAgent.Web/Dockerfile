# Multi-stage build for PersonalAgent.Web
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["PersonalAgent.Web/PersonalAgent.Web.csproj", "PersonalAgent.Web/"]
RUN dotnet restore "PersonalAgent.Web/PersonalAgent.Web.csproj"
COPY ["PersonalAgent.Web/", "PersonalAgent.Web/"]
RUN dotnet build "PersonalAgent.Web/PersonalAgent.Web.csproj" --no-restore -c Release
RUN dotnet publish "PersonalAgent.Web/PersonalAgent.Web.csproj" --no-build -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .

# Environment setup for Railway
ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 5000
ENTRYPOINT ["dotnet", "PersonalAgent.Web.dll"]
