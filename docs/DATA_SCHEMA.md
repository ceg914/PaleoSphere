# 데이터 형식

## 정의 파일 (`data/`)

| 폴더 | 내용 | 스키마 |
|---|---|---|
| `data/species/` | 종 정의 | `data/schemas/species.schema.json` |
| `data/biomes/` | 생물군계. `open_plain`, `forest`, `wetland`, `upland`, `water`는 반드시 있어야 함 | `data/schemas/biome.schema.json` |
| `data/plants/` | 식생 층. 지금은 id 순으로 첫 파일 하나만 사용 | `data/schemas/plant.schema.json` |
| `data/scenarios/` | 세계 설정과 초기 개체군 | `data/schemas/scenario.schema.json` |

스키마 파일은 사람과 편집기를 위한 문서입니다. 실제 검사는 `src/Modding/DefinitionLoaders.cs`가 같은 범위로 수행합니다(외부 스키마 라이브러리를 쓰지 않음).

### 공통 규칙

- `schema_version`은 1.
- `id`는 소문자 영문·숫자·밑줄, 64자 이하.
- 파일 하나는 256 KiB 이하, 중첩 깊이 16 이하, 폴더당 512개 이하.
- 숫자는 항목별 허용 범위 안이어야 합니다. 범위를 벗어나면 그 파일을 건너뛰고 이유를 기록합니다.
- **근거 수준 `evidence`는 필수**입니다.

| 값 | 뜻 | 화면 표시 |
|---|---|---|
| `confirmed` | 화석 기록으로 직접 확인된 내용 | 초록 |
| `inferred` | 화석·현생 유사종에서 추론한 학술적 추정이나 가설 | 주황 |
| `simplified` | 시뮬레이션을 위해 정한 값. 실제 생물의 수치가 아님 | 파랑 |

### 잘못된 파일을 만났을 때

잘못된 파일은 건너뛰고 나머지를 읽습니다. 문제 목록은 시작할 때 창으로 보여 줍니다. 꼭 필요한 정의(필수 생물군계, 식생, 종 하나 이상)가 빠지면 세계를 만들지 않고 이유를 보여 준 채 멈춥니다. 어느 경우에도 프로그램이 바로 꺼지지 않습니다.

### 종을 추가하려면

`data/species/`에 파일을 하나 더 두면 배치 목록과 도감에 나타납니다.

- `diet`가 `herbivore`면 식생을 먹고, `carnivore`면 사체를 먹습니다(0.2.0부터).
- 사냥하게 하려면 `predation.attack_success`를 0보다 크게 줍니다. `carnivore`인데 이 값이 0이면 사냥하지 않고 사체만 찾아 먹습니다.

`predation` 칸(선택):

| 키 | 범위 | 없을 때 | 뜻 |
|---|---|---|---|
| `evidence` | 필수 | — | 근거 수준 |
| `attack_success` | 0~1 | 0 | 공격 한 번의 기본 성공 확률. 초식동물에 0보다 큰 값을 주면 그 파일을 오류로 건너뜀 |
| `defense` | 0~1 | 0 | 사냥감일 때 공격 성공 확률을 `1 − defense/2`배로 낮추고, 이 확률로 반격 |
| `max_chase_seconds` | 1~600 | 40 | 달려서 쫓는 최대 시간(이동 기준 초) |
| `smell_radius_m` | 0~1000 | 120 | 사체를 찾는 반경 |

계산식은 `docs/SIMULATION_SPEC.md` 10장에 있습니다.

## 세이브 파일 (`*.psave`)

gzip으로 압축한 JSON입니다. 압축하지 않은 JSON도 읽습니다.

```json
{
  "format": "paleosphere-save",
  "schema_version": 2,
  "simulation_version": "0.2.0",
  "world_seed": "20261116",
  "saved_at_utc": "2026-11-17T02:20:00Z",
  "state_hash": "…16자리 16진수…",
  "payload_sha256": "…state 바이트의 SHA-256…",
  "display_name": "풍부한 저지대",
  "day": 3, "hour": 14.2, "creature_count": 30, "tick": 13500,
  "state": { }
}
```

`state`에 들어가는 것:

| 항목 | 내용 |
|---|---|
| `config` | 세계 설정 전체(시드 포함) |
| `tick`, `next_creature_id` | 시뮬레이션 시각, 다음 개체 id |
| `world_rng_state`, `world_rng_inc` | 세계 난수 상태(16진수 문자열) |
| `terrain` | 크기, 수면 높이, 생물군계 id 목록, 높이·생물군계·습윤도·토양 배열(Base64) |
| `vegetation` | 바이오매스·토양 영양 배열(Base64), 성장·섭식·고사 누계, 영양 더함·씀·씻김 누계 |
| `carcasses` | 사체(위치, 방향, 사망 원인과 시각, 남은 고기·몸 질량, 처음 질량) |
| `carcass_mass_created`, `_eaten`, `_decomposed` | 사체 질량 장부 |
| `species_ids`, `creatures` | 저장 당시의 종 색인 대응표와 개체 상태 전부(위치, 생리, 행동, 목표, 형질, 난수 상태, 행동 로그, 사냥 상태와 사냥 성공 수) |
| `threats` | 진행 중인 위협 자극 |
| `stats` | 통계 시계열, 사망·배치 누계 |
| `events`, `events_total` | 최근 사건 로그 |
| `user_edits` | 사용자의 지형 수정 내역(편집기가 생길 때까지 빈 배열) |

파생 값(걸을 수 있는 셀, 물까지의 거리, 식생 수용량)은 저장하지 않고 불러올 때 다시 계산합니다.

### 읽을 때의 검사

1. 파일 크기 256 MiB 이하, 압축을 푼 크기 768 MiB 이하, JSON 깊이 32 이하.
2. `format`과 `schema_version` 확인, `state`의 SHA-256이 `payload_sha256`과 같은지 확인.
3. 스키마가 더 새것이면 그 사실을 알리고 거부. 더 옛것이면 `SaveMigrations`의 변환을 차례로 적용.
4. 값 검사: 배열 길이, NaN·범위, 개체 id 중복, 종·생물군계 id 존재, 열거형 범위, 난수 상태 형식.
5. 복원한 상태의 해시를 `state_hash`와 비교. 다르면 경고(정의 파일이 저장 이후 바뀐 경우).

문제가 있으면 예외 대신 오류 목록을 돌려주고, 앱은 백업(`.bak1`~`.bak3`)을 최신 순으로 시도합니다.

### 쓸 때의 절차

`이름.psave.tmp`에 쓰고 디스크에 내린 뒤 다시 읽어 검증 → 기존 백업을 한 칸씩 밀고 현재 파일을 `.bak1`로 복사 → 임시 파일을 제자리로 이름 바꾸기. 검증에 실패하면 원본을 건드리지 않습니다.

### 형식을 바꿀 때

1. `SimVersion.SaveSchema`를 올립니다.
2. `SaveMigrations.Register(이전 버전, 변환)`을 추가합니다.
3. 이전 버전으로 만든 세이브를 `tests/SaveCompatibility/fixtures/`에 남기고 읽기 테스트를 추가합니다. 지금은 `schema1_small.psave`(0.1.0, 초식동물만)와 `schema2_predators.psave`(0.2.0, 개체 13, 사체 3)가 있습니다.

| 변환 | 바뀐 것 |
|---|---|
| 1 → 2 | 빈 `carcasses` 배열을 넣고, 개체의 행동 점수를 6개에서 7개(사냥)로 늘림. 토양 영양 배열과 사냥 상태는 없으면 기본값(0, −1)으로 읽음 |

스키마 1 세이브는 불러올 수 있지만, 0.1.0과 0.2.0은 규칙이 달라 불러온 뒤의 진행은 0.1.0에서 이어 돌린 결과와 같지 않습니다.

## 설정 파일

`user://settings.json`(Windows: `%APPDATA%\PaleoSphere\settings.json`): 언어, UI 배율, 자동 저장 간격, 프레임당 시뮬레이션 한도, 패널 너비, 마지막 탭. 파일이 없거나 깨져 있으면 기본값을 씁니다.
