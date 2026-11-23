# Monitor de Bateria Wireless & Bluetooth (v1.0.0)

Aplicação leve em C# (.NET 10) para exibir na barra de tarefas (System Tray) do Windows o nível de bateria de **múltiplos dispositivos** simultaneamente — incluindo **K-Snake X11 Pro** (Wireless 2.4 GHz com push instantâneo e USB-C), outros mouses Bluetooth LE e fones/periféricos Bluetooth.

## Recursos (v1.0.0)

- **Painel rápido (Flyout Windows 11):** clique esquerdo no ícone abre um pop-up elegante colado na barra de tarefas com cartões de bateria, DPI/Hz, estimativa de autonomia e mini gráfico de histórico.
- **Atualizações em tempo real (HID Push):** detecta instantaneamente no K-Snake X11 Pro mudanças de bateria, entrada/saída da base de carregamento, troca de DPI, Polling Rate (125–1000 Hz) e modo repouso.
- **Múltiplos dispositivos simultâneos:** monitora 2.4 GHz, USB-C, Bluetooth LE e Bluetooth Clássico (HFP) juntos, com opção de **1 ícone com lista** ou **1 ícone por dispositivo** (com indicador discreto de tipo: mouse, áudio, teclado).
- **Personalização por dispositivo:** renomeie dispositivos, escolha cores personalizadas para o ícone ou oculte dispositivos indesejados em *Detalhes, renomear e diagnóstico*.
- **Histórico e estimativa de autonomia:** salva amostras das últimas 72h localmente, gera mini gráfico de descarga/carga e estima tempo restante ou tempo até 100%.
- **Alertas inteligentes:** bateria baixa configurável, carga completa, conectar/desconectar opcional e silenciamento automático durante jogos em tela cheia ou *Não Perturbe* (`SHQueryUserNotificationState`).
- **Estabilidade:** recuperação automática dos ícones caso o Windows Explorer (`explorer.exe`) reinicie (`TaskbarCreated`).
- **Instalador compacto (`WirelessBatterySetup.exe`):** executável único comprimido com Brotli, registro em **Aplicativos Instalados / Programas e Recursos**, atalho no **Menu Iniciar**, **Iniciar com o Windows** e desinstalador limpo.

## Como instalar

Dê duplo clique em **`WirelessBatterySetup.exe`** e clique em **Instalar** (ou **Atualizar / Reinstalar** se já estiver instalado).

## Como recompilar o instalador

```powershell
.\build-installer.ps1
```
