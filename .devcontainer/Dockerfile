FROM mcr.microsoft.com/devcontainers/dotnet
FROM mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator

RUN echo '#!/bin/sh' > /usr/local/bin/cosmos/startemulator.sh
RUN echo 'cd /usr/local/bin/cosmos/' >> /usr/local/bin/cosmos/startemulator.sh
RUN echo 'nohup sh -c ./start.sh &' >> /usr/local/bin/cosmos/startemulator.sh
RUN chmod +x /usr/local/bin/cosmos/startemulator.sh
