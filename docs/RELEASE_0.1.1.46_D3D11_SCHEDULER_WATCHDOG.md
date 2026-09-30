# Ryujinx Xbox UWP 0.1.1.46

Build D3D11-only focada no bloqueio de inicialização observado no Xbox após o primeiro comando FIFO.

- perfil portátil e memória de guest de 4 GiB preservados;
- thread do GPU, heartbeat e watchdog em prioridade `AboveNormal`;
- um tradutor high-CQ em prioridade `Lowest` no perfil UWP;
- yield cooperativo durante rajadas de compilação baseline do ARMeilleure;
- heartbeat não bloqueia mais esperando o mutex da swap chain;
- contadores separados de `Present` bem-sucedido, ocupado e HRESULT;
- consulta periódica de `GetDeviceRemovedReason`;
- telemetria de dispatch e tradução ARM;
- snapshot individual das threads ARM ativas, incluindo core, prioridade, estado e tempo de CPU;
- watchdog de progresso sem encerramento automático do jogo;
- ícones ciano/laranja exclusivos desta versão.
