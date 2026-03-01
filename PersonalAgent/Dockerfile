# Multi-stage build for PersonalAgent
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["PersonalAgent/PersonalAgent.csproj", "PersonalAgent/"]
RUN dotnet restore "PersonalAgent/PersonalAgent.csproj"
COPY ["PersonalAgent/", "PersonalAgent/"]
RUN dotnet build "PersonalAgent/PersonalAgent.csproj" --no-restore -c Release
RUN dotnet publish "PersonalAgent/PersonalAgent.csproj" --no-build -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .

# Environment setup for Railway
ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 5000
ENTRYPOINT ["dotnet", "PersonalAgent.dll"]
