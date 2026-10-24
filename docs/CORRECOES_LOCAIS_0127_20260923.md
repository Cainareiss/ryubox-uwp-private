# Correções locais após os testes da versão 0.2.0.127

## Evidência usada

O ZIP `cloud-logs/RyuboxLogs-0.2.0.127-20260923-215451.zip` não contém as
subpastas `Logs/CoreHost` e `Logs/Ryujinx`. Os traces de pixels presentes nele
são de sessões anteriores. A triagem desta rodada usa também essas subpastas
do LocalState do aplicativo, especialmente as sessões de 24/09 UTC (23/09 local).

- Rivals, 00:46:51 UTC: recusa de commit do JIT, uso 4.961.509.376 bytes,
  limite 5.368.709.120; depois exceção no descarte de `RegionHandle`.
- Pokémon Let's Go, 01:12:05 UTC: último ProcessFrame levou 86.516 ms;
  intervalo registrou 75.321.243 us compilando seis shaders e 6.196.490 us
  criando sete PSOs. A sessão foi encerrada por Stop; esse trecho não contém
  evidência de crash nativo. Há descartes recorrentes do depth incompatível.
- Bomberman: chegou a 1.183 callbacks de apresentação, depois parou de enviar
  comandos; o loop do host continuou vivo. Isso não prova imagem correta nem
  permite atribuir sozinho o bloqueio ao renderizador.

## Alterações

1. `D3D12Window`: aplica o FlipY explícito do produtor ao blit com origem
   superior; remove a inversão extra. Validação no jogo ainda necessária.
2. `D3D12Pipeline`: para alvos 2D single-sample com depth menor, usa proxies
   de cor na interseção, conserva depth/stencil originais e copia a cor de
   volta. Até oito proxies reutilizados por slot/dimensão/formato, liberados
   com o renderer. Não redimensiona coordenadas nem escreve fora da interseção.
   Arrays, MSAA e outras incompatibilidades ainda não são cobertos por isso.
3. `RegionHandle`: descarte idempotente sob lock, evitando a segunda exceção
   durante cleanup da sessão. O teste antigo que esperava exceção agora
   verifica o estado descartado diretamente.
4. JIT: uma recuperação de admissão também no perfil normal de dispatch;
   solicita limpeza/GC e mede o uso novamente antes de repetir a alocação.
   Apenas o modo pinned faz recuperação síncrona de código. Não libera código
   ativo no perfil normal nem aumenta a cota de memória.
5. Orçamento: uma recusa real de alocação pode passar pelo cooldown de trim;
   a amostra explícita atualiza também o cache usado na admissão.
6. Screenshot: sempre retorna o backbuffer apresentado, mesmo preto. A antiga
   substituição pela textura guest ignorava crop/orientação e mascarava falhas.
7. Host diagnóstico: aceita runtime isolado e debug layer por parâmetro.

## Testes executados

- D3D12PresentationRegression, WARP e GPU física: oito combinações de
  crop/flip/resize, conferindo cada pixel; captura de crop preto; depth menor
  com near/far, leitura do depth original, borda preservada e reutilização do
  proxy. Zero erros da debug layer.
- TrackingTests: 80 aprovados, incluindo descarte concorrente/repetido.
- JitCacheRegression: 156 verificações existentes e três verificações novas
  de cooldown/recuperação aprovadas. Não equivale a confirmar Rivals corrigido.

## Reteste local de Bomberman

- CoreHost NativeAOT recompilado em 23/09 às 22:44; runtime isolado em
  `artifacts/local-fixes-20260923/runtime`, sem alterar o aplicativo instalado.
- Sessão curta: 180 s após Start retornar, 1.479 apresentações; encerramento
  controlado, sem device loss. As capturas amostradas foram pretas. Isso é
  **falha de validação visual**, não aprovação por contagem de callbacks.
- Sessão estendida: comparar `present_image_check.txt` (textura guest) e
  captura do backbuffer. Nos presents 300, 600, 900 e 1.200, a textura guest
  também tem energia zero; não há evidência de imagem pronta perdida apenas
  na apresentação. Antes do primeiro draw houve carregamento de arquivos e JIT.
- Amostra externa de 3 s: thread guest `<MainThread>` consumiu 2.922 ms de CPU;
  thread gráfica teve 0 ms nessa amostra. Isso não identifica o PC guest nem
  prova deadlock: houve novas apresentações posteriormente.
- Regressão ampliada: apresentação depois de draws com depth, readback e
  reutilização do proxy também aprovada no WARP e Intel, sem erro de debug layer.

Critério do usuário: passar do loading e obter captura real do menu inicial.
Splash, callbacks e encerramento sem exceção não satisfazem esse critério.

## Reteste de Binder e Bomberman apos a contencao

- O parser de `Parcel` valida cabecalho, intervalos, tamanhos, alinhamento e
  overflow antes de ler; transacoes Binder malformadas sao recusadas sem
  propagar `ArgumentException`/`OverflowException` ate encerrar o CoreHost.
- CoreHost NativeAOT Release recompilado; o binario publicado e a copia no
  runtime isolado foram comparados por hash e sao identicos.
- Nova sessao D3D12 do Super Bomberman R permaneceu ativa e produziu captura
  real do backbuffer. As 04:56:07 UTC foi capturado
  `artifacts/local-fixes-20260923/captures/bomberman-long-p00008.png`:
  55.959 de 56.460 amostras coloridas, energia 42.283.590. O log de jogo da
  sessao nao registra o crash Binder anterior.
- Resultado e progresso, nao aprovacao: depois da captura do logo, uma captura
  posterior (`bomberman-fixed-p00008.png`, 04:59:32 UTC) mostra a tela de menu
  com elementos 2D presentes, mas textos/linhas e partes do layout aparecem
  corrompidos ou incompletos. Assim, foi vencida a tela preta/boot neste teste,
  mas nao ha imagem correta nem criterio de qualidade aprovado. A contencao
  Binder elimina uma causa de encerramento observada; nao corrige o render.
- O relato de que D3D11 iniciava esses mesmos titulos e um baseline util para
  isolar diferencas de backend, mas nao justifica fallback automatico: comparar
  o mesmo jogo/estado/log, pois um backend pode iniciar e ainda apresentar
  imagem incorreta.

## Limites

Os testes acima não certificam que todos os jogos 3D estão corrigidos. Ainda
é necessário retestar Bomberman, Rivals, a transição do tutorial de Pokémon,
Horizon/Cuphead/Captain Toad e o enquadramento de Alien Hominid. A orientação
do jogo precisa ser conferida por captura real, além do teste sintético.

Referência para a escolha do proxy de cor: cópias parciais de depth/stencil
não são permitidas por [CopyTextureRegion](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12graphicscommandlist-copytextureregion).
