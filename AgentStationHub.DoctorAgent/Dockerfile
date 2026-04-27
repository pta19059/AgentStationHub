FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
# Doctor app config baked-in. Foundry's Hosted Agent runtime does not
# (yet, as of 2026-04-26) ingest agent.yaml's environmentVariables block
# nor azd env into the container, so we hard-code the Foundry account
# endpoint + deployment name here. The agent's instance MSI gets a
# bearer for cognitiveservices.azure.com via DefaultAzureCredential.
ENV AZURE_OPENAI_ENDPOINT=https://agenticstationfoundry.openai.azure.com/
ENV AZURE_OPENAI_DEPLOYMENT=ash-doctor

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY AgentStationHub.DoctorAgent.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish AgentStationHub.DoctorAgent.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "doctor-agent.dll"]
