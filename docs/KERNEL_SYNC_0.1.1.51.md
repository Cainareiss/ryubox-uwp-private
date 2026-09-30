# Ryujinx Xbox Kernel Sync 0.1.1.51

Esta versão trata o travamento reproduzível observado nos Logs (10), depois da
inicialização do áudio e antes da segunda submissão GPFIFO.

## Correções

- restaura `Stopwatch/QPC` como fonte monotônica global de alta resolução;
- mantém o contador guest por instância e sincronizado;
- preserva Safe-JIT e o perfil portátil 720p para isolar a correção;
- registra a etapa exata de cada SVC e o proprietário da seção crítica do kernel;
- registra sinais, esperas, dispatches e command buffers da fila GPFIFO;
- inclui `kernel-sync.log` e `gpfifo.log` no diagnóstico do watchdog.

## Identificação

- pacote: `0.1.1.51`;
- nome no Xbox: `Ryujinx Xbox Kernel Sync`;
- backend: D3D11 nativo;
- perfil: portátil, 1280x720.
