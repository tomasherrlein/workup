# WorkUp: datos variados para la demo local

## Objetivo y alcance

Pedido del usuario: cargar ejemplos de turnos, pagos, insumos y otros módulos; eligió incluir acceso de clienta con reservas propias. Es una carga operativa explícita sobre la demo local, no una ampliación del MVP ni del comando T07. No se ejecuta al iniciar la app.

Preparar un lote SQL adicional, insert-only, transaccional y repetible para ambos negocios ficticios: clientes, turnos pasados/presentes/futuros, cobros, gastos, insumos, sesiones de clase, abonos/períodos y cuentas demo de clienta vinculadas por ID. Excluir tokens/enlaces privados e invitaciones sintéticas que no serían utilizables. Emails `.invalid`, sin SMTP.

## Restricciones

- Conservar todas las filas previas y la contraseña de dueña; abortar ante colisiones incompatibles, no corregir ni borrar datos del usuario.
- Solo LocalDB y catálogo dedicado `WorkUpDemo*`; los dos slugs y owners ficticios deben existir.
- Destino manual existente: `WorkUpDemoLocal1222f629b401`, keyring `WorkUpDemoT12KeysLocal1222f629b401`; reutilización únicamente para continuar la demo manual, nunca para tests opt-in.
- Cada corrida real de verificación usa DB y keyring nuevos, retenidos. Prueba de idempotencia dentro de una única corrida, no repetir una corrida fallida sobre ese par.
- No Chrome/navegador, no SMTP, push, PR ni despliegue. Preservar los tres documentos ajenos sin seguimiento.
- Sin modificar código web, migraciones ni contrato de semilla T07. El script usa constraints, enums y relaciones del esquema existente.

## Ruta, unidades y comprobaciones

Ruta directa delegada: exploración de 4+ archivos completada por scout; preparación SQL por un worker; ejecución/validación por verificador independiente. No SDD ni revisión RDD (desactivada para este clon). TDD del código de aplicación no aplica a esta carga operativa: no se modifican servicios; la prueba exigida es SQL real y estados HTTP observados, no una alegación RED/GREEN inventada.

Estimación inicial: un lote SQL de aproximadamente 250–400 líneas y documento de progreso. Sin PR; separar unidades locales solo si el lote lo requiere, no minificar ni omitir verificaciones por tamaño.

- [x] **D01 — Preparar lote insert-only** (`WorkUp/scripts/LocalDemoEnrichment.sql`). Fechas relativas a calendario local, identidad por ID, formas SQL válidas, cupos y horarios coherentes. Preparación no ejecuta SQL.
- [x] **D02 — Validar en pareja nueva**. Migrar/sembrar solo nueva base demo; correr lote una vez y repetirlo dentro de la misma verificación para observar cero inserciones posteriores. Validar FK/constraints, cuentas y reservas propias, agregados por negocio y conservación de filas previas. Retener DB/keyring si falla, sin reintentar.
- [ ] **D03 — Cargar demo manual**. Conservar datos actuales, ejecutar solo lote ya comprobado, verificar conteos y pantallas con HTTP sin navegador. Informar estado del host y credenciales de clienta sin persistir contraseña en archivo.

Rollback: antes del commit SQL, rollback transaccional del lote; después, conservar los datos y no limpiar mediante DELETE/DROP. El script debe impedir duplicación sin actualizar registros existentes.

## Progreso y evidencia

- Scout completó mapa: T07 solo crea negocios/owners/servicios/trabajadoras/grilla, no datos operativos. Agregar al T07 rompería su comprobación exacta de datos previos; se conserva sin cambios.
- Usuario eligió «Sí, dueña y clienta».
- D01: worker preparó `LocalDemoEnrichment.sql` (641 líneas). Todavía no ejecutado ni aceptado: revisión independiente previa detecta posibles guardas que no detienen el lote, aislamiento declarado sin `SET` y frecuencia de abonos sin suficientes clases fijas. Corrección y runtime pendientes; todavía no se insertaron ejemplos.
- Evaluación nativa de riesgo no disponible por archivos sin seguimiento; se trata como riesgo alto y se mantiene verificación independiente. RDD no se activó.
- Tamaño real supera la estimación inicial. Usuario eligió explícitamente «Un único lote completo (recomendado)»: estrategia `exception-ok` para preservar atomicidad de ambos negocios; sin PR ni push, sin minificar ni quitar verificaciones.
- Revisión previa confirmó guardas `RAISERROR` no terminantes antes del TRY, falta de chequeo LocalDB/aislamiento explícito y posible sobrecupo al reutilizar sesiones previas. También se corrigirán fechas incoherentes y cuentas de Marina sin reservas futuras. Frecuencia del abono con menos clases fijas es legal por dominio; se mejorará la plausibilidad, no se presentará como infracción.
- Worker corrigió B1/B2/C1–C6. Lote actual: 879 líneas, guards `THROW` dentro del TRY, LocalDB e aislamiento explícitos, cupos planificados y alcance por IDs demo, fechas coherentes, cuentas de Marina con reservas futuras. Verificado solo en fuente; cero SQL ejecutado hasta ahora.
- D02: SQL real inicial pasó en `WorkUpDemoEnrichVerifyc0d1cde9` y keyring `WorkUpDemoT12KeysEnrichVerifyc0d1cde9`, nuevos y retenidos. Una ejecución agregó datos y una repetición dentro de la misma corrida agregó 0 filas. Filas previas/centinelas intactos y todas las invariantes consultadas con 0 violaciones. Guard de catálogo incorrecto pasó en pareja nueva `WorkUpGuardEnrich82ce8155`/`WorkUpDemoT12KeysGuardEnrich82ce8155`: rechazo con cero filas.
- D02 permanece abierta: worker eliminó la dependencia temporal de los guards de repetición (901 líneas); exige reservas futuras solo para negocios insertados en la invocación actual. Nueva corrida independiente en curso, con pareja inédita, repetición normal y envejecida +40 días. Login HTTP real aún pendiente: el primer verificador descartó la contraseña generada antes de autenticar; solo comprobó forma/copias de hash. En la nueva corrida se mantendrá la contraseña únicamente en memoria del driver hasta probar los logins, sin reseed/update.
- D02 final **PASS real** sobre SHA256 `55ab95aa2af46f08c7f2fcb4984b0c87eaceb6145899ae82e000a0f91089f670`, pareja nueva retenida `WorkUpDemoEnrichFinal000e8f92`/`WorkUpDemoT12KeysEnrichFinal000e8f92`: carga normal, repetición y variante envejecida +40 días con idénticas 41 firmas/conteos, 18 invariantes con 0 violaciones y referencias previas intactas. Marina 14 turnos/14 reservas/6 pagos; yoga 12 sesiones/30 reservas/5 pagos/5 inscripciones/4 períodos; cada negocio 6 clientes/2 cuentas/4 gastos/3 insumos.
- HTTP real del mismo cohort: 2 dueñas y 2 clientas autenticaron con 302; 20 GET de paneles devolvieron 200. Cada clienta vio reservas futuras e historial. El password solo vivió en memoria del driver. Los hosts de verificación quedaron detenidos y los artefactos retenidos; no se usó Chrome ni se afirma prueba de interacción/AX/AT.
- Rama de trabajo `feat/workup-demo-data` creada desde `d155358`; README documenta comando explícito y cuentas demo. Sin PR ni push.
- Espejo Engram pendiente: la herramienta informó `session has already ended` en llamadas previas.

## Próximo paso

Aplicar D03 en la demo manual existente preservando filas previas; verificar accesos y dejar host loopback activo. No ejecutar de nuevo ningún cohort de prueba retenido.
