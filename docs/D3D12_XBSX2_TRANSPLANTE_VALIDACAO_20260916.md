# Transplante D3D12 inspirado no XBSX2 — validação do Ryubox

Atualizado em 16/09/2026.

## Resultado do transplante

Foi adaptado ao Ryubox o conceito seguro de cache de grupos de samplers do
XBSX2. Configurações idênticas são reutilizadas dentro do frame-slot protegido
pela fence; quando o slot é reciclado, o cache também é descartado. Isso evita
copiar o mesmo conjunto de samplers repetidamente sem permitir que uma lista
GPU ainda em execução observe descritores sobrescritos.

Na regressão com a Intel 8086:5916:

- 39.612 views e apenas 8 alocações de sampler em 8 submissões;
- 1 página de sampler, 0 wraps e 39.620 descritores recuperados;
- o teste equivalente de gráficos com query aberta passou;
- o modo fixo anterior precisou de 56 samplers e 32 submissões.

Isso comprova redução de pressão e cópias de descritor. Não é, sozinho, uma
medição de FPS do jogo.

## Componentes que não foram ativados

O cache persistente de PSO foi prototipado, mas está desativado por padrão.
O teste cold-cache serializou um blob; no warm-cache houve `0xC0000409` na
Intel e a liberação provocou `0xC0000005` no WARP. Ativá-lo agora poderia
introduzir crashes. Ele precisa de uma estratégia própria de chave,
invalidação e liberação antes de entrar no caminho normal.

O subalocador de heaps e o ring fence-aware de descritores continuam sujeitos
às validações já registradas no relatório principal. Nenhum binário do XBSX2,
header ou ABI foi copiado diretamente.

## Teste do Rivals of Aether

Jogo testado:

`E:\jogos para testar ryuboxz\Rivals of Aether [0100D2700DE7C000][v0].nsp`

O teste usou a DLL nativa do transplante em um runtime isolado, sem ativar o
PSO experimental. Resultado observado:

- bootstrap concluído em aproximadamente 96 segundos;
- 38 apresentações D3D12, 33 inícios de draw e 24 draws registrados;
- pico de working set de aproximadamente 1,19 GB;
- nenhum `VirtualAlloc(1455)`, `E_OUTOFMEMORY`, exceção ou crash no trace;
- não houve evidência de chegada ao menu ou a uma luta: o jogo permaneceu no
  carregamento infinito relatado pelo usuário.

Portanto, o transplante melhorou a infraestrutura de descritores e mostrou
que o backend apresenta frames sem o erro de commit, mas ainda não corrigiu o
bloqueio de inicialização do Rivals. O próximo diagnóstico deve capturar o
estado do loader/CPU/firmware e os recursos solicitados durante o carregamento;
mais aumento de descritores não é uma correção comprovada para esse sintoma.

Evidências:

- [regressão de descritores](../artifacts/xbsx2-adapt-descriptors-hardware.log)
- [runtime isolado do teste](../artifacts/direct-d3d12-host-rivals-sampler-cache-child2-20260916/)
- [análise arquitetural do XBSX2](D3D12_XBSX2_ANALISE_20260916.md)

## Conclusão de desempenho

Não é tecnicamente honesto afirmar “99% de chance de rodar liso no Xbox”
enquanto o mesmo jogo ainda trava no carregamento no PC e não houve sessão
equivalente no Series S. A melhoria comprovada nesta rodada é de alocação e
pressão de descritores; FPS sustentado, tempo de boot e compatibilidade do
Rivals continuam pendentes.
