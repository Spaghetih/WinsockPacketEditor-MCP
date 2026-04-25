<div align="center">
<p><img src="https://www.wpe64.com/web_images/wpe.png" height="150"></p>

# Winsock Packet Editor (WPE x64)

<img src="https://img.shields.io/github/license/x-nas/WinsockPacketEditor" alt="License"></img>
[![Visitors](https://visitor-badge.laobi.icu/badge?page_id=x-nas.WinsockPacketEditor&title=Visitors)](https://github.com/x-nas/WinsockPacketEditor)
![GitHub Repo stars](https://img.shields.io/github/stars/x-nas/WinsockPacketEditor?style=dark)
![GitHub Repo forks](https://img.shields.io/github/forks/x-nas/WinsockPacketEditor?style=dark)
[![Release](https://img.shields.io/github/v/release/x-nas/WinsockPacketEditor?sort=semver)](https://github.com/x-nas/WinsockPacketEditor/releases)

&bull; <a href="https://www.wpe64.com">Site officiel</a>

</div>

## [📚] Présentation

WPE x64 est un logiciel Windows capable d'intercepter et de modifier les paquets WinSock. Il s'adapte automatiquement aux programmes cibles 32 bits et 64 bits, prend en charge deux modes — proxy SOCKS et injection de processus — et propose des filtres avancés ainsi que des robots automatisés. Le développement utilise le multithreading C# et une file de messages : plus d'un million de paquets ont été interceptés sans gel ni plantage.

WPE x64 sait injecter directement un processus Windows pour intercepter ses paquets Winsock, ou les capturer en mode proxy SOCKS.

## [🎖️] Fonctionnalités

- [x] Deux modes de capture (proxy SOCKS et injection de processus) couvrant la majorité des situations.
- [x] En mode proxy : prise en charge des principaux protocoles de proxy et SSL, mappage de ports, débogage par points d'arrêt.
- [x] Robots automatisés programmables : exécution d'instructions prédéfinies à la rencontre de conditions de déclenchement.
- [x] File de messages mise en cache : tous les paquets entrent en file MQ FIFO, l'affichage n'attend pas la fin de la capture.
- [x] Choix des types de paquets à intercepter : APIs WinSock 1.1 et 2.0 incluses.
- [x] Injecteur et éditeur indépendants : on peut injecter plusieurs programmes et collecter leurs paquets séparément.
- [x] Possibilité d'injecter un programme **non encore lancé** afin de capturer ses paquets dès le démarrage.
- [x] Comparateur de paquets intuitif, bascule rapide entre plusieurs formats de données.
- [x] Recherche dans le contenu des paquets, plusieurs formats de recherche supportés.
- [x] Envoi par lots de paquets avec ordre/cycles personnalisables, import/export et notes.
- [x] Filtres puissants (filtres avancés inclus), longueur et nombre de modifications paramétrables.
- [x] Possibilité d'injecter un programme proxy Winsock pour récupérer ensuite les paquets de la cible.
- [x] Injection directe d'émulateurs : récupération des paquets de l'émulateur et des programmes qu'il exécute.
- [x] Sauvegarde automatique de la configuration : restauration au prochain démarrage.
- [x] Journalisation temps réel et exportable, utile pour diagnostiquer.
- [x] Support natif Windows 64 bits et cibles 64 bits ; choix automatique des DLL 32/64 bits selon le processus cible.
- [x] Les assemblies .NET utilisées n'ont pas besoin du GAC, ce qui simplifie le déploiement et le développement.
- [x] Multithreading : le traitement des paquets ne perturbe pas le fonctionnement normal du programme.
- [x] Désaccrochage propre des hooks et libération des ressources en fin de capture.
- [x] Aucun risque de fuite mémoire ou de ressource sur le processus cible.
- [x] Détection automatique du framework .NET requis lors de l'installation.
- [x] Publication via Microsoft ClickOnce (installation et mises à jour en ligne).
- [x] Multilingue.

## [🤖] Mode headless + serveur MCP pour Claude

Ce dépôt contient désormais une **version headless** (sans interface) accessible via un serveur **Model Context Protocol** afin de piloter WPE x64 depuis Claude Code, Claude Desktop ou tout client MCP.

Voir [WPE.Headless/README.md](WPE.Headless/README.md) pour les instructions d'installation et la liste des outils MCP exposés (`list_processes`, `inject_process`, `get_packets`, `send_packet`, `get_stats`, `stop_capture`).

## [🖼️] Captures d'écran

<img width="1200" height="750" alt="Accueil" src="https://github.com/user-attachments/assets/fbdaba92-edf8-486c-905a-a92ebb523ea2" />

<img width="1450" height="802" alt="Liste des paquets" src="https://github.com/user-attachments/assets/59f26b9d-e6df-4ccd-b3a4-9807d7db5ba8" />

<img width="1450" height="802" alt="Statistiques" src="https://github.com/user-attachments/assets/9e5f5330-ebe0-4b3f-92eb-c13ec6329a78" />

<img width="1450" height="802" alt="Robot" src="https://github.com/user-attachments/assets/b7eb16b7-fee1-4381-8b7f-0ab6e49287a4" />

## [⚖️] Avertissement légal

Cet outil est destiné aux tests réseau **autorisés**, à l'analyse de protocoles, au reverse engineering pédagogique et à la recherche défensive sur des cibles que vous possédez ou pour lesquelles vous avez obtenu l'autorisation écrite. L'utilisation contre des services tiers sans autorisation peut violer les conditions d'utilisation, le droit local sur l'intrusion informatique et entraîner des poursuites. Vous êtes seul responsable de l'usage que vous en faites.

## [👏] Note spéciale

Ce projet est référencé dans [DotNetGuide](https://github.com/YSGStudyHards/DotNetGuide) et fait partie de l'organisation [dotNET China](https://gitee.com/dotnetchina).
