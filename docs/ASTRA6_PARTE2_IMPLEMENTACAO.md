# Astra6 — Parte 2: D3D11/GPU

Status em 2026-09-09: candidato 0.2.0.117 em validacao local. Gate 2 NAO certificado.
As secoes iniciais preservam o historico da primeira rodada; a atualizacao ao final registra as implementacoes seguintes e pendencias atuais.

Plano: `C:\Users\Cainã\Downloads\Ryubox_Astra6_Parte_2_D3D11_GPU.md`.
Base: 0.2.0.116. O usuario validou melhora da Parte 1, mas reportou crash de Steins;Gate sem diagnostico definitivo. Nao atribuir automaticamente esse crash ao D3D11.

## Implementado nesta rodada

### Copias com escala/conversao (item 8, parcial)

- Texturas criadas pelo renderer recebem o caminho de blit GPU; views preservam essa ligacao.
- Escala point/linear, retangulos invertidos e conversao entre formatos de cor suportados usam shaders D3D11, sem readback intermediario.
- Suporte inicial: R8/RG8 UNORM, RGBA8/BGRA8 UNORM/sRGB, R/RG/RGBA float16/32 e fontes comprimidas que o D3D11 possa amostrar, com destino de cor suportado. BC1 para RGBA foi validado com pixels reais.
- Copia sem escala entre formatos diferentes tambem usa conversao GPU, em vez de CopySubresourceRegion entre formatos incompativeis.
- Copia direta de retangulo usa mip/layer fisicos da view e sinaliza a escrita no destino.
- Estados temporarios de constant buffer e sampler do blit sao restaurados; referencias COM sao liberadas inclusive em falha de restauracao.
- Ainda faltam variantes integer, depth/stencil, MSAA, destinos comprimidos e views multidimensionais. Fallback CPU legado permanece explicito para casos nao implementados; alguns ainda podem rejeitar o pedido. Nao afirmar que Link's Awakening/DMC3 estao corrigidos sem testar seus pedidos reais.

### Feedback/alias (item 9, parcial)

- Geracoes de escrita agora pertencem ao storage fisico e sao indexadas por subresource, nao isoladas por objeto view.
- Escrita por alias invalida o snapshot correspondente. Escrita em outro mip nao invalida esse snapshot.
- Snapshots de uma view 2D de um unico mip/layer podem ler subresources fisicos diferentes de zero.
- Upload view-local agora aponta para o mip/layer fisico correto; uploads parciais notificam somente o subresource escrito.
- Draw/clear notificam o mip efetivamente enderecado pelo RTV/DSV, nao todos os mips do SRV pai.
- Contadores: gpuScaledCopies, cpuScaledCopyAttempts e feedbackReuses. Trace de feedback inclui mip, layer e motivo da invalidacao; bytes comprimidos sao contados por bloco.
- O snapshot continua por view e preserva a politica de snapshot do render pass. Compartilhamento de snapshots entre views equivalentes, invalidacao de UAVs e reducao medida nos jogos outliers ainda estao pendentes. Nao alegar reducao global em GB a partir do teste sintetico.

### DrawTexture (item 10, parcial)

- Nao exige mais array de attachments com comprimento exatamente 1. O alvo 0 valido pode estar em um array de oito slots.
- Ausencia do alvo 0 ou objeto de backend incorreto continua sendo erro, agora com tipos, attachments e pressao de memoria no diagnostico. Nao e um retorno silencioso que mascara imagem ausente.
- Falha ao criar snapshot de uma sobreposicao nao produz deliberadamente uma imagem preta: reporta o caso nao suportado.
- TMNT ainda precisa de regressao no jogo; falha de BSD/rede e assunto separado da Parte 3.

## Validacao local

`eng/D3D11MaterialRegression` executa D3D11 WARP sem janela, ROM, keys ou firmware. O readback utilizado pelo teste serve para conferir o resultado pixel a pixel, nao faz parte do caminho de copia GPU.

Foram aprovadas 30 verificacoes, incluindo escala 2x2 -> 4x4, interpolacao linear, RGBA -> BGRA, flip solicitado, attachments com oito slots, BC1 -> RGBA, ausencia de fallback CPU nos caminhos testados, snapshots de mip 1, alias, upload view-local e regressoes de uniform buffers, swizzle, viewport/scissor e X3018.

Evidencias: `artifacts/parte2-d3d11-tests.log` e `artifacts/parte2-corehost-build.log`.

## Preservado / proximo trabalho

- Sem alteracoes no SetMatrixTransform, orientacao global de apresentacao, input A ou no frontend.
- Nenhum jogo foi aberto nesta rodada; nenhuma instalacao, MSIX novo ou desligamento foi solicitado/executado para a Parte 2.
- Restam os casos de copia acima, hazard cache completo e metricas nos jogos, stalls/syncpoints, shaders em loops dinamicos, storage Store, R16G16B16Float, Cuphead sem readback no caminho normal e DMA invalido.
- Repetir a matriz de regressao do plano antes de liberar pacote e antes de iniciar a Parte 3.

## Atualizacao de 2026-09-09 - candidato 0.2.0.117

Pedido atual: concluir pendencias, gerar MSIX e instalar no PC. Nao desligar nesta rodada.
**As regressoes locais abaixo nao certificam os jogos do Gate 2.** O pacote e candidato de teste, nao garantia de compatibilidade dos 50 jogos.

### Implementacoes adicionais

- **8 / scaled copy:** ampliada a lista de formatos normalizados/float aceitos pelo blit. Ainda faltam variantes integer, depth/stencil, MSAA, destino comprimido e views multidimensionais. O fallback CPU legado permanece explicito. Link/DMC3 precisam dos pedidos reais reproduzidos.
- **9 / hazards:** snapshots agora compartilhados por storage + mip + layer + formato, com referencias independentes e liberacao pelo ultimo dono. Views equivalentes nao provocam outra copia. Escritas UAV por compute/draw invalidam texture snapshots e mirrors de buffers; bind nao conta como escrita. Unbind de imagem aceita null. Mantida a politica de snapshot do render pass. Apenas 2D/single mip/layer/single sample/cor suportados; formatos diferentes permanecem conservadoramente separados.
- **9 / metricas:** created/reused/invalidated, bytes, full/partial, CPU submission ms, GPU scaled copies e CPU fallback attempts. Submission ms NAO e tempo de conclusao na GPU; nenhuma reducao de GB em jogos e alegada a partir dos testes sinteticos.
- **10A / stalls:** FIFO agora reporta tempo ocupado inclusivo, espera por query D3D11, espera por syncpoint guest, compilador ativo/tempo, frame e pressao de memoria. Esperas >=100 ms registram motivo e IDs. Tempo de FIFO pode incluir compilacao/waits: nao somar categorias como tempos exclusivos da GPU. Preservados disk cache e orcamento/serializacao do compilador; nenhum sleep/limiter adicionado.
- **11 / loops:** lowering conservador de Sample para SampleGrad em kernels 2D scalarizados. Prova de uniformidade considera atribuicoes, condicoes, dependencias de loops e componentes half empacotados. Somente deslocamento aditivo uniforme pode ser removido da derivada. Nao usa SampleLevel(0), unroll artificial ou conversao de coordenadas divergentes.
- **11 / evidencias:** fixture Arashi transformada compila com FXC e flags de producao. **A fixture original tambem compilou neste PC**, portanto X3511 no Xbox nao foi reproduzido aqui. AM2R ainda necessita do shader reproduzivel. Em falha HLSL, salvar hash/stage/SPIR-V/HLSL/bindings/diagnostico em `Logs/d3d11-shader-failures`, deduplicado e limitado a 32 shaders. SPIR-V e identificado como IR traduzido, nao ISA original guest.
- **12/13:** preservadas correcoes de interface e bindings. TEXCOORD16/31 continua compilando no teste. Global-memory Store nao foi reescrito sem reproducer novo; Winter Burrow ainda precisa de regressao de jogo.
- **15 / half3:** stream IA separado converte seis para oito bytes, com W half(1). Cache por source/generation/offset/size/stride. Preserva stride zero e divisor; nao le o quarto half do proximo vertice. Usa mirror CPU quando valido e readback quando a GPU modificou a fonte. Nao e compute repack/raw fetch. Conversoes liberadas ao retirar atributos/encerrar pipeline; ausencia de slot IA livre continua erro explicito. Testes verificam bytes e criacao real do input layout; Luigi ainda precisa de gameplay.
- **16 / Cuphead:** Cuphead nao esta na lista atual de forceCompatibility. Testado contrato de source 1920x1080 para host 1366x768, copia + Present + callback e zero chamadas ao fallback. Isso NAO mede cpuReadbackFrames em uma sessao real de Cuphead.
- **17 / orientacao:** flag GAL -> ABI nativa centralizada preservando `!crop.FlipY`. Testes cobrem ambos os flips e pixels do presenter nativo. SetMatrixTransform e input/frontend nao foram modificados.
- **18 / DMA:** preservados semaphore-only e fills constantes sem fonte, incluindo registradores observados em Marvel. Adicionadas verificacoes de sentinel/overflow do intervalo linear antes do acesso. Acesso guest invalido continua falhando: nao ha silencio global. Logs incluem regioes XYZ, dimensoes, pitch, raw block registers e GOB Y/Z. A guarda nao prova que toda causa de DMA invalido foi resolvida.

### Regressoes locais finais

| Suite | Resultado |
| --- | --- |
| D3D11MaterialRegression (WARP/FXC) | 48 verificacoes aprovadas |
| DmaPolicyRegression | 16 verificacoes aprovadas |
| JitCacheRegression | 31 verificacoes aprovadas |
| PtcRegression | 15 verificacoes aprovadas |
| RyuboxNativePresentationSmoke | Inicializacao, blits legacy/V2, crop, flip, presents, isolamento frontend, resize e duas sessoes aprovados |

Evidencias: `artifacts/parte2-final-*.log`, `artifacts/parte2-native-presentation.log`, `artifacts/parte2-corehost-build.log` e `artifacts/parte2-msix-build.log`.
O readback dos testes compara pixels e nao foi adicionado ao caminho normal de apresentacao.

### Pendencias do Gate 2

- Executar Link/DMC3, Luigi, TMNT, AM2R/Arashi, Marvel, Cuphead, Odyssey, Mario 3D World e Soul Reaver no dispositivo alvo.
- Comparar bytes/copias/crashes antes/depois nos outliers e medir BOTW warm-cache versus Resident Evil cold-cache.
- Cobrir as variantes de copia/feedback restantes conforme os pedidos reais dos jogos.
- Validar ausencia de DEVICE_HUNG/CreateRTV/CreateSRV, regressao de Store e orientacao em gameplay.

Nao iniciar Parte 3 tratando estes itens como certificados.

### Empacotamento e instalacao confirmados

- MSIX 0.2.0.117 gerado e assinatura validada por SignTool.
- Pasta: `artifacts/xbox/Release/AppPackages/Ryujinx.Uwp.App_0.2.0.117_x64_Test`.
- SHA256 MSIX: `3F353F1F42EB1CB072BCF01F56D52B671D0DE7B58A9BE08E6821E1E95879E738`.
- CoreHost publicado, empacotado e instalado coincidem: `E17B499451B4C6FDC3181C78B55F8ABD18D9DB1EC6A1EBD02EE0343E3AAE7DE1`.
- Exports Start/StartWithHandle/Stop/SetInput/InstallFirmware/GetSessionState verificados no NativeAOT.
- Add-AppxPackage concluiu; Windows confirmou `Ryujinx.Xbox.UWP_0.2.0.117_x64__z7hww2mmeny5j`, `Status: Ok`.
- Atualizacao sobre a instalacao existente, sem desinstalar o app ou apagar dados. Nenhum jogo/frontend aberto nesta rodada; PC nao desligado.
- Evidencia adicional: `artifacts/parte2-install-verification.log`.

## Correcao CPU/JIT posterior ao pacote 0.2.0.117

A auditoria de `Logs (6)` e `D3D11ShaderCache (1)` levou a uma rodada adicional de correcoes de locks de compilacao, politica de reclaim JIT e interrupcoes do dispatcher. Detalhes e evidencias em `docs/ASTRA6_LOGS6_CORRECOES.md`.

43 testes JIT, 15 de PTC e 13 de arenas passaram; CoreHost Release compila sem erros. Essas alteracoes ainda nao foram publicadas em NativeAOT, empacotadas ou instaladas. Desempenho e progressao dos jogos no Xbox continuam pendentes de medicao; nao fechar o Gate 2 com base apenas nas regressoes locais.

## VSync e itens 1/2 da auditoria — posterior ao pacote 0.2.0.117

Implementadas propagacao de VSync ate o Present nativo, correcoes de tamanho/polling/vida util e reset de queries, e materializacao adiada de constant buffers dinamicos mantendo 64 KiB e cauda zerada. Foram ampliadas as regressoes com draws, compute, contagem nao zero de oclusao, eviction e alocacoes. Detalhes, limites e evidencias em [ASTRA6_VSYNC_QUERIES_CONSTANTES.md](ASTRA6_VSYNC_QUERIES_CONSTANTES.md).

71 verificacoes D3D11 WARP aprovadas; smoke nativo com SyncInterval 0/1 e build CoreHost Release sem erros. Ainda sem NativeAOT/MSIX/instalacao destas alteracoes e sem medicao de FPS no Xbox. O CoreHost novo requer a DLL nativa nova por causa do export de apresentacao com intervalo; nao misturar artefatos de versoes diferentes.

## Atualizacao: MSIX 0.2.0.118 concluido

As pendencias de publicacao/empacotamento acima foram atendidas na rodada 0.2.0.118: CPU/JIT, VSync/queries/constantes e novas otimizacoes de readback, interop, trim, logs e diagnostico de audio. NativeAOT publicado; MSIX assinado e verificado com identidade x64 e hashes das duas DLLs conferidos. Sem instalacao no PC.

160 checks de regressao e smoke nativo aprovados. High-CQ/re-JIT e shaders paralelos continuam desativados apos avaliacao dos riscos de invalidacao e teardown. Nao declarar a auditoria inteira ou Gate 2 certificados; desempenho/progressao no Xbox e as linhas de pesquisa restantes ainda precisam de validacao.

Entrega, hashes, testes, riscos e pendencias: [ASTRA6_DESEMPENHO_118.md](ASTRA6_DESEMPENHO_118.md).
