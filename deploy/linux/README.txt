RLT Recorder for Linux (window) — @VERSION@
==========================================

For a Linux machine with a desktop — a Raspberry Pi 4/5 with Raspberry Pi OS
Desktop (64-bit), a laptop — recording telemetry from an Xbox, PlayStation or
another PC on the same network.

Which file
----------
  PC / laptop (Intel, AMD)        ->  rlt-recorder-gui-linux-x64.tar.gz
  Raspberry Pi 4/5, 64-bit OS     ->  rlt-recorder-gui-linux-arm64.tar.gz

A machine without a screen wants the command-line version instead, in
rlt-recorder-cli-linux, which also installs itself as a service.

Start
-----
1. Unpack it wherever you like:

       tar xzf rlt-recorder-gui-linux-arm64.tar.gz

2. Run it:

       ./rlt-recorder/RltUdpClient

3. Optional, to get it in the desktop menu:

       ./rlt-recorder/add-to-menu.sh

It starts recording as soon as it opens.

Where things are
----------------
Recordings:  ~/RLT Recorder/
Settings:    ~/.config/rlt-recorder/config.json

In the game
-----------
Telemetry settings > UDP Telemetry: On, UDP IP Address: this machine's IP
(hostname -I), port 20777, format 2025.

If nothing arrives, a firewall may be dropping it. On a machine with ufw:

    sudo ufw allow 20777/udp
    sudo ufw allow 20780/tcp

The recordings can also be downloaded from another device in a browser at
http://<this machine's IP>:20780

Included libraries
------------------
libICE.so.6 and libSM.so.6 are the X.Org libraries from Debian 12, shipped so
that the window also starts on minimal systems that lack them. Licences:
COPYING.libice6, COPYING.libsm6.
