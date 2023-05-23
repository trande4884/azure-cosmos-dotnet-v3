FROM mcr.microsoft.com/devcontainers/universal
FROM mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator
RUN \
    --memory 3g --cpus=4.0 \
    --name=test-linux-emulator \
    --env AZURE_COSMOS_EMULATOR_PARTITION_COUNT=10 \
    --env AZURE_COSMOS_EMULATOR_ENABLE_DATA_PERSISTENCE=true \
    --interactive \
    --tty \
    mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator