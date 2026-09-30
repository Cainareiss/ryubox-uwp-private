# Ryujinx Xbox UWP 0.1.1.57 — Video + Audio Trace

Build D3D11-only para validação no Xbox Dev Mode.

## Correções

- Lazy guest commit alterado de 4 KiB para 16 KiB, reduzindo em 75% a quantidade de `VirtualAlloc(MEM_COMMIT)` e a pressão de VM/VAD observada no Xbox.
- Erros de commit agora preservam o código Win32 real, endereço, tamanho, thread, working set e mensagem completa.
- Saída XAudio2 limitada a estéreo; o compat layer do Ryujinx faz o downmix 5.1 para 2 canais antes de enviar ao Xbox.
- Telemetria de áudio registra bytes não nulos, pico PCM16, HRESULT de submit, frames consumidos e erro interno da source voice.
- Scissor D3D11 só é habilitado depois de receber retângulos válidos.
- Viewports com largura/altura negativas são normalizados para valores aceitos pelo D3D11.
- Removida a segunda conversão de profundidade no SPIRV-Cross, pois o tradutor do Ryujinx já faz essa conversão.
- Telemetria nativa registra ownership, thread gráfica, descrição da swap chain, tentativas/sucessos de copy, upload e Present, além dos HRESULTs finais.
- Descrição de cada frame inclui contadores de draw/dispatch/clear, programa, render targets, viewport, scissor e rasterizer discard.

## Validação local de 23 de agosto de 2026

- Pacote 0.1.1.57 instalado e aberto com sucesso.
- Shovel Knight carregado pelo core: `Application Loaded: Shovel Knight: Treasure Trove v3.00`.
- Lazy commit de 16 KiB chegou a 372.260.864 bytes sem nenhuma falha registrada.
- Duas voices XAudio2 estéreo foram criadas e iniciadas com HRESULT `0x00000000`.
- O XAudio2 consumiu mais de 61.200 frames sem voice error.
- Os buffers produzidos pelo jogo durante a janela observada eram silêncio (`nonZeroBytes=0`, `peakInt16=0`).
- O guest continuou executando, mas o GPFIFO permaneceu no primeiro dispatch e não chegou a Copy/Present.
- O processo local terminou quando o PC ficou sem capacidade de commit suficiente; antes do teste havia apenas cerca de 2,3 GiB de memória virtual livre. Isso não reproduz o limite exato de 5 GiB do Xbox.

## O que conferir nos logs do Xbox

- `resource-trace.log`: `lazyCommitFailures=0` e granularidade de 16384.
- `compositor.log`: campos `copy`, `present`, `lastCopy`, `lastPresent` e descrição da swap chain.
- Log mais novo em `Logs/Ryujinx`: linhas `XAudio2 UWP buffer` com `nonZeroBytes` e `peakInt16`.
- `d3d11-frame.log`: pixels coloridos e estado do pipeline no momento da apresentação.

SHA-256 do MSIX: `120856E5FFF0313F6D640F6DCD2A52C8F43F232F1EF48F69EAEB0E39C76FF7FA`.
