# Xbox 0.2.0.135: cache, orientacao e desempenho

Fonte: `C:\Users\Cainã\Documents\logs.zip`, sessoes de 30/09/2026.

## Evidencia

- Settings de todas as sessoes: Handheld, scale=1,00. HleConfiguration recebe
  dockedMode=false. Nao substituir recursos guest por 720p indiscriminadamente.
- Super Bomberman solicita buffers 1280x720; Horizon, Pokemon e outros
  solicitam buffers 1920x1080 mesmo com o perfil portatil. A superficie UWP
  e 1920x1080. Tamanho de buffer nao identifica sozinho a resolucao interna.
- Pokemon, 19:25:55 UTC: 65.784.987 us em compilacao de shaders num intervalo
  de 71.750 ms, 46 misses. Ha custo real de compilacao, nao apenas present.
- Guest shaderCache=False nao desativa o cache nativo DXBC! Sao camadas
  distintas. A explicacao inicial de cache persistente desativado era ampla
  demais; os logs antigos nao comprovam sucesso de leitura/gravação DXBC.
- JIT Xbox: native-pinned-v2; desktop aprovado usava FAST_EPOCH_JIT=1.
  Nao atribuir toda a diferenca de FPS a texturas ou compilacao.
- Primeira amostra de presentAcceptedFps de Alien e 11984, com zero callbacks
  guest: contadores herdados do frontend contaminam a primeira amostra.
  Nao usar medias brutas desse campo como FPS de gameplay.
- Metroid encerra por Guest commit refused, uso=5238444032, limite=5368709120,
  margem=134217728. Ha uma exception nao tratada no servico IPC/audio.

## Correcao implementada nesta rodada

O CoreHost prepara e testa leitura/escrita em AppDataManager.BaseDirPath /
D3D12ShaderCache, passando o caminho por RYUJINX_UWP_D3D12_CACHE_DIR antes
de criar o renderer. Registra sucesso/falha sem tornar o cache obrigatorio.
O nativo deixa de congelar o caminho da primeira compilacao para sempre:
shaders da interface podem ser compilados antes da configuracao do CoreHost.
Isso elimina dependencia de LOCALAPPDATA + identidade fixa para jogos.

Regressao de settings/cache: PASS para caminho, probe removida, repeticao
entre sessoes, caminho vazio e arquivo no lugar de diretorio. Nao prova
cache aquecido no Xbox; e necessario comparar hits/misses numa segunda sessao.

## Pendencias

1. Validar build nativo/CoreHost e cache DXBC frio/quente; verificar acesso
   real no Xbox antes de afirmar ganho de FPS.
2. Corrigir baseline de contadores ao adquirir contexto do frontend.
3. Comparar orientacao de SRV/render target do Professor com Vulkan/D3D11.
   O relato do usuario e de texturas invertidas. Nao aplicar flip global.
4. Perfil JIT precisa de medicao A/B com reclamacao segura, nao liberacao
   indiscriminada de flags experimentais no console.
5. Tratar recusa de commit no caminho IPC de Metroid sem exception fatal.

Sem novo MSIX nesta rodada ate validar os binarios alterados. A versao
0.2.0.135 ja entregue nao contem esta nova correcao de caminho do cache.

## Resultado local

CoreHost build Release passou (0 erros, 30 avisos). Native Release compilou
em `artifacts/cache-path-native-20260930/Release`. Teste
`D3D12InteropRegression --warp --cache-path-only`: PASS para compilacao fria,
arquivo DXBC gravado, hit quente sem novo miss e mudanca de pasta depois
da primeira compilacao. Nao equivale a reteste de desempenho no Xbox.
