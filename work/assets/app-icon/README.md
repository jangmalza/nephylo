# Nephylo app icon

알파벳 없이 원 안의 채워진 면으로 남은 양을 표현하는 흑백 잔량계 픽토그램입니다. 눈금·화살표·장식 없이 굵은 원형 테두리와 수평선으로 작은 트레이·파비콘에서도 읽히도록 디자인했습니다. 심벌의 채움은 브랜드 도형이며 실제 계정 잔여량에 따라 변하지 않습니다. Nephylo 글자는 기존 UI 폰트를 그대로 사용합니다. 서비스 제공업체의 로고와는 별개의 앱 아이콘입니다.

- 원본: `usage-widget.svg`, 64×64 좌표계
- 검은 타일: `#111111`, 흰 심벌: `#FFFFFF`
- 테두리 없는 둥근 타일과 투명한 모서리로 밝고 어두운 바탕에 대응
- 원형 심벌은 64px 좌표계에서 지름 44px, 테두리 두께 8px
- 수평 채움선은 정수 픽셀에 놓이며, 곡선은 4배 해상도 렌더링 후 축소해 매끄럽게 출력

저장소 루트에서:

```powershell
.\work\generate-app-icon.ps1 -OutputDirectory .\work\icon-design-nephylo -PreviewPath .\outputs\Nephylo-Icon-Preview.png
```

`widget.ico`에는 16, 20, 24, 32, 40, 48, 64, 128, 256px 프레임이 들어 있습니다. `widget.png`는 512px입니다. 생성기는 외부 라이브러리나 다운로드 없이 SVG의 사각형·원·다각형을 렌더링합니다.

Windows / .NET Framework의 네이티브 트레이 경로와 호환되도록 16~128px 프레임은 32비트 BGRA DIB와 4바이트 정렬 AND 마스크를 사용합니다. 256px만 PNG로 압축합니다. 작은 프레임을 PNG로 저장하면 `System.Drawing.Icon`에서 색이 깨질 수 있어, 프레임 디코딩뿐 아니라 Windows 네이티브 아이콘 렌더링도 검증합니다.

사이트의 `dist/assets/app-icon.svg`는 이 원본의 복사본이며, `dist/favicon.ico`는 생성된 `widget.ico`의 복사본입니다. 공유 이미지는 `work/website/generate-share-card.ps1`에서 같은 생성기를 호출해 로고를 사용합니다. 웹 아이콘을 바꾸면 HTML의 아이콘·공유 이미지 캐시 식별자도 함께 갱신합니다. 이미 공개된 EXE·ZIP은 다시 생성하지 않으며, 앱 아이콘은 다음 신규 버전 빌드에 포함합니다.
