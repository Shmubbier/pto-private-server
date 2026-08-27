# Hosting on Oracle Cloud (Always Free) — LEGACY (central-server path)

> **This is the archived central-server model, not the current build.** The `steam-p2p`
> branch replaced this with peer-to-peer over Steam's relay: no VM, no port forwarding, the
> server runs locally on each host and `ptolaunch` connects players (see `README.md` and
> `Launcher/README.md`). Keep this guide only if you want a single always-on public server
> instead of P2P. The matching build is on `archive/tailscale` (tag `tailscale-final`).

Runs the game server (TCP 51338) and the website API (HTTP 8080) as one process on one
free Linux VM. The website itself is hosted separately (Firebase Hosting) and just calls
the API over HTTPS.

## 0. What you end up with
- Game clients connect to `PUBLIC_IP:51338` (set in each client's `settings.ini`).
- The website calls `https://api.yourdomain.com/...` (Caddy terminates TLS, proxies to :8080).
- Accounts, decks, ranks, match history persist under `/opt/pto-server/data/`.

## 1. Create the VM
1. Oracle Cloud console -> Compute -> Instances -> Create.
2. Image: **Ubuntu 22.04/24.04** (or Oracle Linux 9). Shape: any **Always Free** shape
   (Ampere A1 arm64, or an AMD `E2.1.Micro`). Both are plenty.
3. Add your SSH public key. Create.
4. Compute -> reserve a **Reserved public IP** and attach it, so it survives reboots.

## 2. Open the ports in the Oracle VCN
Networking -> your VCN -> the subnet's **Security List** (or an NSG) -> add **Ingress** rules,
source `0.0.0.0/0`, protocol TCP, destination ports:
- `51338` (game)
- `80` and `443` (Caddy / Let's Encrypt for the API)

## 3. Install .NET 8 on the VM
SSH in (`ssh ubuntu@PUBLIC_IP`), then:
```bash
# Ubuntu 24.04:
sudo apt update && sudo apt install -y dotnet-sdk-8.0 git
# Ubuntu 22.04 (Microsoft feed):
#   wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb
#   sudo dpkg -i packages-microsoft-prod.deb && sudo apt update && sudo apt install -y dotnet-sdk-8.0 git
# Oracle Linux 9:
#   sudo dnf install -y dotnet-sdk-8.0 git
```

## 4. Build and install the server
```bash
git clone https://github.com/Shmubbier/pto-private-server.git
cd pto-private-server
sudo mkdir -p /opt/pto-server
sudo dotnet publish PtoServer.csproj -c Release -o /opt/pto-server
sudo cp pto-server.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now pto-server
systemctl status pto-server --no-pager      # should be active (running)
journalctl -u pto-server -f                  # live logs (Ctrl+C to stop watching)
```
The service auto-starts on boot and restarts on crash. Data lives in `/opt/pto-server/data/`.

## 5. Open the OS firewall
Oracle images ship with a host firewall on top of the VCN rules.
```bash
# Ubuntu (iptables-based images): allow, then persist
sudo iptables -I INPUT 6 -p tcp --dport 51338 -j ACCEPT
sudo iptables -I INPUT 6 -p tcp --dport 80    -j ACCEPT
sudo iptables -I INPUT 6 -p tcp --dport 443   -j ACCEPT
sudo netfilter-persistent save
# Oracle Linux (firewalld):
#   sudo firewall-cmd --permanent --add-port=51338/tcp --add-port=80/tcp --add-port=443/tcp
#   sudo firewall-cmd --reload
```

## 6. HTTPS for the API (Caddy)
1. Point DNS: create an A record `api.yourdomain.com -> PUBLIC_IP`.
2. Install Caddy and configure it:
```bash
sudo apt install -y debian-keyring debian-archive-keyring apt-transport-https
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | sudo gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | sudo tee /etc/apt/sources.list.d/caddy-stable.list
sudo apt update && sudo apt install -y caddy
sudo cp Caddyfile /etc/caddy/Caddyfile
sudo sed -i 's/api.yourdomain.com/api.YOURREALDOMAIN.com/' /etc/caddy/Caddyfile
sudo systemctl restart caddy
```
Caddy fetches a Let's Encrypt cert automatically. Test:
`curl https://api.YOURREALDOMAIN.com/players` -> `{"online":[],"matches":[]}`.

No domain? You can skip Caddy and call `http://PUBLIC_IP:8080` directly, but a browser page
served over HTTPS (Firebase) cannot call a plain-HTTP API, so a domain + Caddy is the real path.

## 7. Point the game clients
In each player's game folder, `settings.ini`:
```ini
[NETWORK]
IP=PUBLIC_IP
```
Port 51338 is hardcoded in the client. Register / log in as usual.

## 8. The website (Firebase Hosting)
Static site that calls the API. Login flow:
- `POST https://api.YOURREALDOMAIN.com/login` with form body `user=...&pass=...` -> `{token,...}`.
- Store the token; send it as `Authorization: Bearer <token>` on `GET /player/<user>`.
- `GET /players` for the live list (no token needed).

## API reference
| Method | Path | Auth | Returns |
|---|---|---|---|
| POST | `/login` | none | `{token,user,rank,wins,losses}` or 401 |
| GET | `/players` | none | `{online:[user...], matches:[{a,b}...]}` |
| GET | `/player/{user}` | Bearer token | `{user,rank,wins,losses,decks:[{name,cards:[id...]}],history:[{at,opponent,won}]}` |

Notes: CORS is open (`*`); tighten `Access-Control-Allow-Origin` in `HttpApi.Handle` to your
Firebase domain if you want. The token secret is generated once into `data/apisecret.txt`.
Rank semantics: lower is better, 1 is the top (matches the client's rank icon).

## Updating later
```bash
cd ~/pto-private-server && git pull
sudo dotnet publish PtoServer.csproj -c Release -o /opt/pto-server
sudo systemctl restart pto-server
```
