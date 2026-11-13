#!/bin/sh
set -e
mkdir -p /var/lib/aiursoft-dMS
chown www-data:www-data /var/lib/aiursoft-dMS

# Bootstrap config if missing (matching Docker entrypoint pattern)
if [ ! -f /etc/aiursoft-dMS/appsettings.json ]; then
    cp /usr/share/aiursoft-dMS/appsettings.json /etc/aiursoft-dMS/appsettings.json
fi
# Replace /usr/share copy with symlink to /etc
rm -f /usr/share/aiursoft-dMS/appsettings.json
ln -sf /etc/aiursoft-dMS/appsettings.json /usr/share/aiursoft-dMS/appsettings.json
