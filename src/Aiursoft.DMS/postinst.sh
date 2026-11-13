#!/bin/sh
set -e
mkdir -p /var/lib/aiursoft-dms
chown www-data:www-data /var/lib/aiursoft-dms

# Bootstrap config if missing (matching Docker entrypoint pattern)
if [ ! -f /etc/aiursoft-dms/appsettings.json ]; then
    cp /usr/share/aiursoft-dms/appsettings.json /etc/aiursoft-dms/appsettings.json
fi
# Replace /usr/share copy with symlink to /etc
rm -f /usr/share/aiursoft-dms/appsettings.json
ln -sf /etc/aiursoft-dms/appsettings.json /usr/share/aiursoft-dms/appsettings.json
