# Ryujinx Xbox Audio JIT 0.1.1.52

Esta versão corrige duas causas comprovadas pelos Logs (11), sem alterar o
perfil portátil de 720p nem o backend gráfico D3D11.

## Causas confirmadas

- O CoreHost entregava todo áudio a `DummyHardwareDeviceDriver`, que avançava
  os contadores do renderer e descartava os samples. Por isso nenhum jogo
  poderia produzir som audível.
- O dispatch gerenciado do ARMeilleure não chamava `CheckInterrupt()` entre
  funções traduzidas. `RequestInterrupt()` também não zerava o contador
  nativo, permitindo que threads hóspedes ignorassem uma troca de contexto.

## Correções

- backend XAudio2 nativo para UWP/Xbox, sem SDL, OpenAL ou CRT desktop;
- cópia de buffers PCM para memória nativa e liberação em `OnBufferEnd`;
- eventos de conclusão devolvidos ao renderer de áudio;
- interrupção publicada entre threads e contador nativo zerado imediatamente;
- checagem do escalonador após cada função no dispatch gerenciado;
- pontos de sincronização em toda entrada de basic block no Safe-JIT.

## Validações locais

- NativeAOT Release publicado com sucesso;
- host C++ AppContainer compilado e ligado a `XAudio2_9.dll` do sistema;
- teste real de 48.000 frames PCM: submit, start, callback e consumo completos,
  com HRESULT e VoiceError iguais a zero;
- 84 testes focados de CPU/codegen e áudio aprovados, zero falhas.
