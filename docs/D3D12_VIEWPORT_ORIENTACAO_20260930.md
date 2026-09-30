# Orientacao por viewport: implementacao candidata

Relato: cena e menu de opcoes do proprio Pokemon Let's Go aparecem invertidos.
Nao se trata do menu ImGui do emulador.

## Alteracao

- O D3D12 normaliza extents negativos para positivos. Para manter o mapeamento
  GAL/Vulkan, a saida clip-space recebe X *= sign(width), Y *= -sign(height).
- SupportBuffer.ViewportInverse.xy transporta esses sinais quando o transform
  esta habilitado. Atualiza quando muda o sinal entre draws, nao so quando muda
  o enable. Com transform desativado, conserva a normalizacao anterior.
- EmitterContext aplica a operacao apenas para TargetApi.Direct3D12.
  Geometry shaders preservam/restauram X/Y originais entre emissoes.
- Versao de codegen de cache host passa de 7356 para 7357; shaders antigos
  nao podem ser reutilizados com o novo significado das constantes.
- Sem alterar flip de apresentacao, frontend, modo portatil ou escala.

## Validacao executada

- CoreHost build Release: 0 erros, 33 avisos, apos corrigir qualificacao de TargetApi.
- 120 comparacoes matematicas: sinais X/Y, origem deslocada, pontos NDC.
- Teste de quadrantes rasterizados/readback: PASS em WARP e GPU fisica,
  quatro combinacoes X/Y, sem erros da debug layer.
- 15 verificacoes de arrays/cubemaps/fatias 3D: PASS em WARP.

## Limites e proximo aceite

O teste GPU usa HLSL controlado com a mesma operacao matematica; nao executa
o tradutor Maxwell nem a cena do Pokemon. Cull esta desligado nesse teste.
Falta validar a cadeia completa com os shaders guest, transform feedback,
geometry/tessellation e front-face/cull. Nao considerar o jogo corrigido.
Falta publicar NativeAOT e capturar cena e menu do Pokemon, depois repetir
a corrida Horizon aprovada. Nenhum MSIX novo foi produzido.

Depois desse aceite, investigar FPS global: A/B pinned/epoch seguro,
cache DXBC frio/quente e baseline dos contadores herdados do frontend.
