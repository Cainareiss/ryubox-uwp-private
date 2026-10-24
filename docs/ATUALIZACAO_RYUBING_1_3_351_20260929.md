# Ryubox — integração do Ryubing Canary 1.3.351

Data: 29/09/2026. Atualização de código; desempenho e compatibilidade com jogos
no Xbox não foram medidos nesta rodada.

## Fonte e escopo verificados

- Base upstream anterior: `Canary-1.3.341`, commit
  `4c21a617573d19cb72a754e01925778116b85073`.
- Alvo: `Canary-1.3.351`, commit
  `475615f0431b4995b03c930c5089d20f6f3570e0`.
- Objeto da tag: `191d6885bd224dcfe4b3a0f77c47041720d2b161`.
- Fonte de transporte: https://gitlab.hugofnm.fr/publicmirrors/Ryujinx.git
- Tag consultada: https://gitlab.hugofnm.fr/publicmirrors/Ryujinx/-/tags/Canary-1.3.351

O servidor canônico `git.ryujinx.app` estava inacessível. A 1.3.351 foi a tag
mais recente encontrada nesse espelho, não uma confirmação de que o servidor
oficial não possua versão posterior. O histórico baixado contém a base exata
já usada localmente: a ancestralidade foi verificada e os blobs dos arquivos
sem personalização foram conferidos. Isso verifica consistência do conteúdo;
a tag do espelho não é uma assinatura de autenticidade do publicador.

Foram integrados os efeitos finais dos dez commits entre as duas versões,
em 26 arquivos upstream. O MIT e os avisos de licença existentes foram mantidos.

A árvore efetivamente compilada pelo CoreHost fica em
`E:\ports\ryujinx uwp\src`, que é um repositório separado do checkout externo.
Os arquivos de núcleo ficam em `src/src/` relativamente ao workspace externo.
A atualização foi aplicada nessa árvore real.

## Mudanças incorporadas

| Área | Mudança upstream | Efeito esperado |
| --- | --- | --- |
| PTC/JIT | `7cc2975` e `8113028`: identidade de cache por programa/processo, versão e seletor; atualização de versões e diagnósticos | Identifica o proprietário do cache e mantém o carregamento consistente entre sessões. |
| Blend | `9e896b8`: bulk update usa os registradores shadow corretos nos modos track/replay | Restaura o blend gravado mesmo após mudanças no estado corrente. |
| Descritores de textura | `d2fb140`: rejeita endereço zero/não mapeado antes de decodificar formato e dimensões | Impede interpretar um slot guest não inicializado como textura válida. |
| Shaders SPIR-V | `e2a612b`: normalização de coordenadas NaN para zero | Define a coordenada usada pela amostragem em vez de depender do tratamento de NaN do driver. Também afeta o caminho SPIR-V/HLSL do D3D12. |
| Diagnóstico de GPU | `b251318`: versão do driver na GAL/Vulkan; identificação Intel Windows | Logs mais precisos para comparar drivers e adaptadores. |
| Vulkan | Guardas de arrays de buffer textures presentes no resultado final de `368a71c`/`475615f` | Evita tratar buffer views como imagens ou associar samplers a elas. |
| UI desktop | `2b6e077`/`e5712b7`: perfis de controle, associação de dispositivos, layout e seleção OpenGL | Atualiza a fonte da interface Avalonia. Não redesenha a interface ImGui/UWP. |

A criação de dependências de cópia bruta entre texturas, introduzida na
1.3.346 e revertida na 1.3.351, não faz parte do delta final aplicado.

## Adaptações necessárias ao Ryubox

- Preservados os callbacks, probes, cotas, reclaim, despacho pinado e mudanças
  locais de NativeAOT/D3D12. A checagem de cache desabilitado durante o probe
  de acesso guest continua no `Translator`.
- `PtcCacheInfo` agora percorre HLE, contextos CPU, tradutor e PTC.
- Cache executável com versão local `7032` e seletor `-ryubox`;
  `-pinned-v1` continua distinguindo o despacho pinado. O cache desktop e o
  UWP não devem compartilhar código executável, mesmo com versões coincidentes.
- `HardwareInfo` aceita o novo campo de versão de driver como argumento opcional.
  D3D11/D3D12 continuam compilando com três argumentos; versão indisponível
  permanece vazia, sem inventar um número.
- Versão de geração do cache de shaders: `7355 -> 7356`, para não reutilizar
  código host anterior à normalização NaN. O primeiro boot após a atualização
  pode precisar recompilar caches.
- Adicionado `eng/RyubingUpdateRegression` e acesso de testes aos estados GPU.

## Validação realizada

- CoreHost Release e dependências, incluindo D3D11/D3D12: **PASS**, zero erros,
  quatro avisos em D3D11/serviços de texto.
- `RyubingUpdateRegression`: **23 verificações PASS**. Grava/reabre perfis
  reais no disco, verifica dois programas da mesma aplicação em quatro sessões
  com PIDs diferentes, versões, seletores, cache desabilitado e programa desconhecido.
  Exercita o método real de bulk blend nos modos track, passthrough, replay e
  track-with-filter, incluindo notificação de estado sujo e replay redundante.
- `JitCacheRegression`: **276 verificações PASS**, incluindo commit/reclaim,
  execução guest, concorrência, publicação exclusiva e probes de diagnóstico.
- Promoção para a árvore principal: os **30 arquivos de código/teste** conferem
  com os blobs da cópia validada; `git diff --check` aprovado.
- Publicação NativeAOT win-x64: **PASS**, processo encerrado com código zero.
  DLL nativa PE x64 conferida, sem header CLR, com exports Start,
  StartWithHandle, Stop, SetInput e HasFirstVisiblePresent presentes.
  Tamanho: 36.854.784 bytes. SHA-256:
  `5853D62EDE017BB83DD54387151320AB3C1332D2FE0268C9623BBE5D27AD3D3B`.
  Dependência `libSkiaSharp.dll` presente na saída.
  O compilador reportou 43 avisos IL e 12 mensagens de métodos que sempre
  lançarão exceção; as mesmas contagens aparecem no log anterior
  `za-probe-v3-publish-20260928.log`. Incluem reflexão/dynamic e PLINQ:
  são limitações pendentes, não foram eliminadas por esta atualização.

Não foram executados jogos, medições de FPS, testes Xbox, empacotamento MSIX,
instalação ou validação visual da UI desktop/UWP nesta rodada. A compilação
gerenciada não substitui esses testes. As 23 verificações não são um teste
completo de renderização por pixels nem de todos os descritores GPU.

## Reproduzir os testes

Na raiz real do repositório (`E:\ports\ryujinx uwp\src`):

```powershell
dotnet build Ryujinx.Uwp.CoreHost/Ryujinx.Uwp.CoreHost.csproj -c Release -m:1 --no-restore
dotnet run --project eng/RyubingUpdateRegression/RyubingUpdateRegression.csproj -c Release
dotnet run --project eng/JitCacheRegression/JitCacheRegression.csproj -c Release
```

## Preservação e evidências

- Cópia upstream: `artifacts/reference-ryubing-canary-1.3.351-20260929/`.
- Worktree isolada: `artifacts/ryubox-ryubing-1.3.351-integration-20260929/`,
  branch `codex/ryubing-canary-1.3.351-20260929`.
- 59 arquivos locais relevantes copiados para a integração antes do delta.
  Os 57 fora da lista upstream permaneceram idênticos na checagem anterior à promoção.
- Originais dos arquivos atualizados:
  `artifacts/ryubing-1.3.351-original-files-20260929/`.
- Diff local anterior: `artifacts/ryubing-1.3.351-original-local-diff-20260929.patch`.
- Logs: `artifacts/ryubing-1.3.351-corehost-build.log`,
  `ryubing-1.3.351-regression-build.log`, `ryubing-1.3.351-regression.log`,
  `ryubing-1.3.351-jit-build.log`, `ryubing-1.3.351-jit-regression.log` e
  `ryubing-1.3.351-corehost-publish.log`.
- Saída NativeAOT concluída: `artifacts/ryubing-1.3.351-corehost-native/`.
- Nenhum reset, descarte de mudanças, commit ou push automático foi feito.
