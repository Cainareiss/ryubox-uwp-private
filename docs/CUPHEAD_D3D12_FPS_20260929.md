# Cuphead: teste direto D3D12 com contador de FPS

## Alteração

- O CoreHost pode mostrar os FPS no título da janela Win32 local, com
  `RYUJINX_UWP_LOCAL_WINDOW_FPS=1` (switch `-Fps` do runner).
- A amostra usa os presents nativos aceitos no intervalo, não iterações FIFO
  nem callbacks guest. Não mede frames distintos efetivamente exibidos pelo monitor.
- A amostragem ocorre na thread GPU e envia uma mensagem assíncrona à janela;
  a atualização do título não espera a UI na thread GPU.
- A amostra é atualizada aproximadamente a cada segundo enquanto o loop progride.
  Se o loop bloquear, o título conserva a última amostra: não é um watchdog.
- O runner agora processa mensagens Win32 para teclado/título/fechamento,
  mostra explicitamente a janela mesmo com console auxiliar oculto e permite
  `-DurationSeconds 0` para teste interativo sem timeout automático.

## Validação

- Parse PowerShell, compilação do interop C#, processamento de mensagens e
  detecção do fechamento de janela de teste oculta: PASS.
- Lookup da janela real por classe/PID: PASS. O título nulo fica no C# para
  evitar a conversão de `$null` em string vazia pelo PowerShell.
- NativeAOT Release win-x64: exit 0, com avisos de trimming/AOT já existentes.
- DLL nova: `artifacts/cuphead-d3d12-fps-runtime-20260929/Ryujinx.Uwp.CoreHost.dll`.
- SHA-256: `323174D6D77AF03C7F9B70642DEF47199D7E9DD5DD48B5529AF9A36154F1EE1B`.
- Processo do teste carregou CoreHost, native e SPIRV-Cross da pasta isolada.
- Cuphead iniciou com D3D12 de hardware, HWND 1280x720, teclado, áudio e PTC.
- Primeiros dois presents confirmados nos logs. A captura inicial da janela
  ainda está preta e mostra 0.0 FPS; isso NÃO valida menu, gameplay ou 60 FPS.
- Às 04:27:23Z, a sessão avançou para 140 presents aceitos, com 1 FPS na
  última amostra. A captura nativa do frame 120 mostra o fundo escuro com
  textura de película da introdução, não o menu nem gameplay. Arquivo:
  `artifacts/cuphead-d3d12-fps-20260929.png`.

## Repetir

Na raiz externa `E:\ports\ryujinx uwp`:

```powershell
.\src\eng\Run-CoreHostD3D12Direct.ps1 `
  -GamePath 'E:\jogos para testar ryuboxz\Cuphead [0100A5C00D162000][v0].nsp' `
  -RuntimeDirectory '.\artifacts\cuphead-d3d12-fps-runtime-20260929' `
  -Keyboard -Fps -Audio -Ptc -DurationSeconds 0
```

Controles: setas = direcional, Enter/Z = A, X = B, C = X, V = Y,
WASD = analógico esquerdo, P = +, O = -, Q/E = L/R, 1/3 = gatilhos.

Logs desta execução: `artifacts/cuphead-d3d12-fps-20260929.stdout.log`,
`artifacts/cuphead-d3d12-fps-20260929.stderr.log`; compilação:
`artifacts/cuphead-d3d12-fps-publish-20260929.log`. Telemetria do jogo:
`LocalState/Logs/CoreHost/performance.log`, filtrando o título `0100A5C00D162000`
e o início da sessão em `2026-09-29T04:23:26Z`.
