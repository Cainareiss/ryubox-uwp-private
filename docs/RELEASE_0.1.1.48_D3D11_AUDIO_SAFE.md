# Ryujinx Xbox UWP 0.1.1.48 - D3D11 Audio Safe

Esta build trata o bloqueio reproduzido no Xbox imediatamente depois de
`AudioRenderer Start`.

## Alteracoes do runtime

- ativa `Ryujinx.Uwp.SafeAudioRenderer` somente no CoreHost UWP;
- limita cada espera de resposta do DSP a 250 ms;
- remove o pacing dependente de `Stopwatch/QPC` do caminho Xbox;
- usa um periodo externo de 5 ms para o relogio de audio;
- se o DSP nao responder, continua sinalizando os eventos do AudioRenderer;
- impede que uma falha de audio bloqueie indefinidamente o encerramento;
- adiciona logs de handshake, heartbeat, timeout e ativacao do fallback;
- mantem Safe-JIT, modo portatil, limite de 5 GiB e D3D11 nativo.

O fallback e silencioso porque o port UWP ja utiliza o backend de audio dummy.
Ele prioriza manter o guest em execucao e permitir que o jogo alcance o
primeiro frame.

## Identidade visual

- versao do pacote `0.1.1.48`;
- nome instalado `Ryujinx Xbox Audio Safe`;
- selo `A48` e indicador `AUDIO SAFE 0.1.1.48` no cabecalho;
- tema laranja/roxo para diferenciar esta build das anteriores;
- D3D11 aparece como backend inicial na interface.

## Linhas esperadas no proximo log

```text
Xbox safe audio renderer enabled: bounded DSP waits, 5 ms external clock and event fallback
Starting audio renderer with Xbox safe clock (250 ms DSP watchdog, 5 ms update period)
Audio processor handshake completed; external safe clock owns pacing
Xbox safe audio heartbeat: cycles=1, fallback=False
```

Se o DSP bloquear, o log deve registrar `DSP watchdog fired` e os heartbeats
seguintes devem mostrar `fallback=True`.
