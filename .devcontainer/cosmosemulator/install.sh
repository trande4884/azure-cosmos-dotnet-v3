#!/bin/sh

echo '#!/bin/sh' > /usr/local/bin/cosmos/startemulator.sh
echo 'cd /usr/local/bin/cosmos/' >> /usr/local/bin/cosmos/startemulator.sh
echo 'nohup sh -c ./start.sh &' >> /usr/local/bin/cosmos/startemulator.sh
chmod +x /usr/local/bin/cosmos/startemulator.sh
nohup '/usr/local/bin/cosmos/startemulator.sh'