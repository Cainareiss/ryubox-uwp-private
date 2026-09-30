# Astra6 — rodada de desempenho 0.2.0.118

## Escopo entregue em código

Esta rodada acrescenta às correções CPU/JIT e VSync/queries/constantes posteriores à 0.2.0.117:

- Readback de buffers por intervalo alinhado, com staging reutilizado e crescimento sob demanda. Uma leitura desalinhada de 4 bytes de um buffer de 4096 bytes copia 8 bytes.
- Leitura pelo mirror quando válido; leitura de contador indireto usa span na stack. Não se baixa conteúdo antigo antes de uma substituição CPU integral. Escrita parcial continua preservando bytes produzidos pela GPU.
- Uploads `UpdateSubresource` passam o span diretamente durante a chamada síncrona, sem array e pin intermediários. O caminho de constant buffers continua com 64 KiB, dirty slices, DISCARD e cauda zerada.
- Cache de delegates COM por endereço de função e assinatura, com último endereço por thread. O cache não retém objetos device/context. Endereços diferentes usam entradas diferentes.
- Um único controlador de trim/GC em `RuntimeMemoryBudget`, cooldown de cinco segundos, proteção contra reentrada e escalada com intervalo mínimo. Compaction de LOH fica restrita à emergência. O handler CoreHost não dispara outro GC.
- Trim do cache DXBC por bytes: preserva até 8 MiB em High e 4 MiB em Critical; Emergency pode liberar tudo. Cache em disco é mantido. Não é LRU nem mecanismo de precompilação.
- Logging diagnóstico em lotes a cada 250 ms, limitado a 1024 registros e aproximadamente 2 MiB de texto pendente. Fatal/errors são barreiras síncronas; término normal da sessão/processo drena a fila. Overflow e falhas de escrita contam em `diagnosticRecordsDropped`. Um encerramento abrupto pelo sistema pode perder o lote normal ainda pendente; não existe garantia de flush após kill/OOM.
- Análise de sinal PCM completa nos primeiros oito buffers e amostrada a cada 32 depois disso. Submissão, PCM, callbacks de consumo e pool de áudio não foram amostrados ou removidos. Alertas passam a descrever amostras de silêncio, não tempo exato.
- Color mask zero não invalida snapshots do color attachment em draws. Depth/stencil/UAV continuam conservadores. Não há reutilização de feedback depois de uma escrita real.
- O build verifica obrigatoriamente o export nativo `PresentRendererFrameWithSyncInterval`, evitando publicar CoreHost novo com DLL nativa antiga.

Orientação, crop, SetMatrixTransform, controle, resolução, High-CQ, proteção de pins e limites JIT não foram relaxados para elevar números de FPS.

## Testes

| Suíte | Resultado |
| --- | --- |
| D3D11MaterialRegression | 78 checks; D3D11 WARP real, pixels/draw/compute, query, swizzle, cópia, mask, readback e constantes |
| JitCacheRegression | 43 checks, incluindo invalidação concorrente e pin de código realmente executado |
| PtcRegression | 15 checks |
| ArenaLifecycleRegression | 17 checks, incluindo cooldown/reentrada de trim e ordem/limite do logging |
| AudioPoolRegression | 7 checks em XAudio2 real; 300 reenvios sem crescimento de packets |

No microteste de 1000 atualizações consumidas de uniform, a alocação gerenciada medida caiu de 224.000 bytes na rodada anterior para 0 nesta execução aquecida. Não é uma medição de alocações de todo frame, de custo nativo do driver ou de FPS de jogo.

Evidências: `artifacts/performance-rest-{d3d11,jit,ptc,memory,audio,native}.log` e `artifacts/performance-118-msix-build.log`.

## O que não está certificado/concluído

A auditoria também contém linhas de pesquisa, não uma lista de ganhos garantidos. Permanecem dependentes de novos perfis e implementação específica:

- Shaders: separar tradução/compilação em worker limitado e preaquecer conteúdo conhecido. Não foi habilitada compilação paralela global; Sonic estava lento mesmo com cache HLSL aquecido.
- CPU: mudança de qualidade do código, High-CQ/rejit e dispatcher nativo com pins exigem comparação e validação próprias. Esta versão inclui a rodada segura de locks/reclaim/interrupções, não uma remoção de proteções.
- Readbacks de textura por mip/layer, repacks GPU e caches de ASTC/ETC2 não foram reescritos nesta rodada. Leitura obrigatória guest continua síncrona; não foi criada uma fila universal de readback assíncrono.
- Dirty state completo, GPU timestamps de cópias e equivalência integral de todos os contadores de primitives permanecem fora da validação realizada.
- Jogos sem frames e gates de progressão da Parte 2 ainda exigem logs da versão atual e execução no Xbox.

Portanto, **não declarar desempenho máximo atingido, todos os itens da auditoria concluídos, nem Sonic Mania a 60 FPS antes do teste do console**.

## Avaliação de ativação: High-CQ e shaders paralelos

Solicitada durante o empacotamento. Resultado: **não habilitar os caminhos atuais globalmente**. Isso é uma avaliação de código e regressões existentes, não um crash novo reproduzido no Xbox.

1. `Translator.BackgroundTranslateCore` chama `Translate(... highCq: true)` sem capturar uma geração de invalidação e depois publica em `Functions.AddOrUpdate`. A proteção de geração implementada/testada no caminho frio `AcquireFunction` não está aplicada ao worker. Uma invalidação pode ocorrer durante a compilação e a publicação posterior reinstalar código antigo. Pins protegem lifetime; não tornam uma tradução antiga semanticamente válida. Safe-JIT atualmente impede essa ativação e conserva zero workers. Desligá-lo altera também decoder/contexto de tradução, não somente uma opção de otimização.
2. `D3D11SpirvCrossCompiler.CompileCore` usa contexto por chamada, mas `Dispose` só muda `_disposed` e chama `NativeLibrary.Free`, sem drenar operações em andamento. Introduzir workers requer ownership/lease do compilador e shutdown com join. `D3D11Program` ainda compila e cria objetos no mesmo construtor; não tem publicação assíncrona aguardável. Adicionar Task.Run não permite que um draw dependente prossiga corretamente por si só.
3. `RuntimeMemoryBudget.CompilerGate` tem capacidade 1. Abrir paralelismo de HLSL exige orçamento de scratch por trabalho, admissão/cancelamento e testes com pressão de memória. Apenas aumentar threads pode aumentar filas ou OOM, sem reduzir compilação serializada.

Pré-requisitos para outra rodada: repro de invalidação concorrente no re-JIT, rejeição/reciclagem de traduções obsoletas, política/cache versionados; separação dos estágios CPU de criação/bind GPU; worker limitado, leases da biblioteca, cancelamento e teste de encerramento durante compilação. Depois medir startup frio e warm gameplay no Xbox. Os 43 testes JIT desta build validam o modo seguro atual, não certificam High-CQ concorrente.

### Roteiro do console

Usar o pacote identificado e manter a mesma resolução/configuração para comparar. Ativar o contador de FPS e testar a mesma fase de Sonic Mania duas vezes, distinguindo cache frio/quente. Registrar alguns minutos de gameplay, áudio, frametime e retorno ao menu. Não aumentar o contador com frames duplicados nem usar limitador como prova de melhora.

Enviar os logs novos com o resultado (FPS durante a fase, não só menu/splash). A versão anterior instalada no PC não muda só por gerar o MSIX; não foi solicitada instalação nesta rodada.

## Pacote concluído e verificado

- MSIX x64: `artifacts/xbox/Release/AppPackages/Ryujinx.Uwp.App_0.2.0.118_x64_Test/Ryujinx.Uwp.App_0.2.0.118_x64.msix` (112.989.074 bytes).
- SHA256 MSIX: `7348EC29435D450857BF93FDB8E47D1CA50C23EADB7DDC2C7769C9C8EF24D8E8`.
- CoreHost NativeAOT publicado e empacotado: `2604B4E794C2847A94B67F93E56D1C64CAFD2BB125077CD5ACB9825A6FC8A3D0`.
- Native DLL empacotada coincide com a testada: `E432C3AB1A6F67D908C6F0A5C7871B12843315AD7D3DE85D4D6F5DD1A0CD9BC1`.
- Assinatura SignTool verify /pa: aprovada, zero erros/avisos de verificação. Certificado na mesma pasta, junto das dependências.
- Exports Start/StartWithHandle/Stop/SetInput/InstallFirmware/GetSessionState presentes no CoreHost publicado.
- Smoke nativo aprovado novamente, incluindo VSync 0/1, isolamento frontend, crop/flip e duas sessões.
- Evidência: `artifacts/performance-118-package-verification.log`. Verificador reutilizável: `eng/Verify-Performance118Package.ps1`.
- 160 checks nas cinco suítes listadas, além do smoke nativo. Há avisos preexistentes de trimming/AOT/serialização no publish; sucesso do build não substitui regressão dos jogos no Xbox.
- Nenhuma instalação, abertura de jogo/frontend ou desligamento do PC realizado nesta rodada.

## Retorno do console: Logs (5)

A rodada seguinte confirmou desempenho insuficiente (Sonic: média de 13,43 FPS
nas últimas 30 amostras). A auditoria e a candidata 119 estão documentadas em
`docs/ASTRA6_LOGS5_DESEMPENHO_119.md`. O novo caminho usa dispatcher nativo com
pins; não reativa High-CQ/re-JIT concorrente nem reverte as correções gráficas.
Não interpretar os testes sintéticos da 118 como certificação de 60 FPS no Xbox.
