# 라이선스 기록

## 이 저장소의 코드와 데이터

저장소 소유자가 아직 라이선스를 정하지 않았습니다. 공개 배포 전에 정해야 합니다.

## 외부 에셋

| 파일 | 원본 | 제작자 | 라이선스 | 수정 여부 |
|---|---|---|---|---|
| `assets/models/dinosaurs/Triceratops.fbx` | Animated Dinosaur Pack (2018), <https://quaternius.com/packs/animateddinosaurs.html> 의 다운로드(Google Drive) FBX 폴더. 2026-11-17 받음 | Quaternius | CC0 1.0 | 원본 그대로. 크기·방향·자세 보정은 실행 중에 코드로 한다 |

라이선스 근거:

- 팩 안의 `License.txt`(저장소에 `assets/models/dinosaurs/QUATERNIUS_LICENSE.txt`로 함께 둠)에 **CC0 1.0 Universal**이라고 적혀 있고, 팩 소개 페이지에도 License: CC0로 표시되어 있습니다.
- 다만 quaternius.com의 일반 라이선스 페이지는 2026-08-28자 **Quaternius Asset License(QAL) v1.0**으로 바뀌어 있습니다. QAL은 게임에 넣어 배포하는 것은 허용하지만 에셋 자체를 따로 재배포하는 것은 금지합니다. QAL 7조는 "변경은 이미 받은 에셋에 소급하지 않는다"고 적고 있고, 이 팩은 CC0 문서와 함께 배포되고 있어 CC0로 판단했습니다.
- 이 판단이 걱정되면 모델 파일을 저장소에서 빼고 빌드할 때만 받아 오도록 바꿀 수 있습니다. 모델이 없으면 게임은 코드로 만든 메시로 자동 대체합니다.

같은 팩의 다른 5종(티라노사우루스, 벨로키랍토르, 스테고사우루스, 아파토사우루스, 파라사우롤로푸스)은 아직 저장소에 넣지 않았습니다. 육식동물을 추가할 때(Phase 2) 같은 절차로 넣습니다.

코드로 만든 것:

| 항목 | 출처 | 비고 |
|---|---|---|
| 모델이 없을 때 쓰는 개체 메시 | 코드로 생성(`CreatureMeshBuilder`) | `src/Presentation/` |
| 식물(양치류, 속새, 덤불, 침엽수, 활엽수, 소철류) | 코드로 생성(`PlantMeshes`) | `src/Presentation/PlantMeshes.cs` |
| 물 표현 | 직접 작성한 셰이더 | `assets/shaders/water.gdshader` |
| 지형 색·식생 표시 | 직접 작성한 셰이더 | `assets/shaders/terrain.gdshader` |
| 아이콘 | 직접 작성한 SVG | `icon.svg` |
| 글꼴 | 운영체제에 설치된 글꼴을 실행 중에 불러 씀(Windows: 맑은 고딕). 파일을 배포하지 않음 | `src/UI/UiTheme.cs` |

모델·음향을 추가할 때는 위 표에 원본 링크, 제작자, 라이선스, 받은 날짜, 수정 여부를 적고, 사용 권리가 확인되지 않은 자산은 넣지 않습니다.

## 엔진과 라이브러리

| 이름 | 버전 | 라이선스 | 쓰임 |
|---|---|---|---|
| Godot Engine (.NET) | 4.7.2 stable | MIT | 엔진, 내보내기 템플릿 |
| GodotSharp / Godot.NET.Sdk | 4.7.2 | MIT | C# 바인딩(NuGet) |
| .NET 런타임 | 8.0 | MIT | 실행 파일에 함께 들어감(self-contained) |
| xunit | 2.9.3 | Apache-2.0 | 테스트 전용 |
| xunit.runner.visualstudio | 2.8.2 | Apache-2.0 | 테스트 전용 |
| Microsoft.NET.Test.Sdk | 17.11.1 | MIT | 테스트 전용 |

Godot로 만든 실행 파일을 배포할 때는 Godot의 라이선스 고지와 엔진이 포함한 제3자 구성 요소의 고지를 함께 실어야 합니다(<https://docs.godotengine.org/en/stable/about/complying_with_licenses.html>). 현재 빌드에는 이 고지 화면이 없으므로 **배포 전에 추가해야 합니다.**

## 종 정보의 출처

종 정의 파일(`data/species/*.json`)의 `sources` 항목에 적습니다. 체격 수치처럼 2차 자료를 종합한 값은 그 사실을 함께 적었습니다.
