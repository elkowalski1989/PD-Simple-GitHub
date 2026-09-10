;; ==========================================================================
;; Signal Categories for DP Via Corridor Check
;; ==========================================================================
;; Format: (list "SKILL_REGEX_PATTERN" "CATEGORY" priority "RISK_LEVEL")
;; Matched against UPPER-CASED net names using rexMatchp
;; Priority: lower number = checked first, first match wins
;;
;; SKILL regex notes (NOT PCRE):
;;   \( \) for grouping, \| for alternation
;;   [0-9] instead of \d, [A-Za-z0-9_] instead of \w
;;   * = zero-or-more, + = one-or-more
;;   ? is literal (not "optional") -- use \(...\) or separate patterns
;;   ^ $ are anchors, . matches any char
;;
;; Risk levels:
;;   CRITICAL - >100kHz signals, resets, enables — impedance discontinuity,
;;              crosstalk, skew from corridor violations
;;   MEDIUM   - Borderline signals (UART, some I/O)
;;   LOW      - Static/slow signals (GPIO, LED, NC, power)
;; ==========================================================================

(setq dpvc_signal_categories (list

  ;; ================================================================
  ;; Priority 0: No-connect / mounting / test coupons (static = LOW)
  ;; ================================================================
  (list "^NC_"                                "NC"            0 "LOW")
  (list "^NC[0-9]"                            "NC"            0 "LOW")
  (list "^NU_"                                "NOT_USED"      0 "LOW")
  (list "^NOCONNECT"                          "NC"            0 "LOW")
  (list "^NO_CONNECT"                         "NC"            0 "LOW")
  (list "^PD_"                                "NC"            0 "LOW")
  (list "^PU_"                                "NC"            0 "LOW")
  (list "^PUD_"                               "NC"            0 "LOW")
  (list "^MH[0-9]"                            "MOUNTING"      0 "LOW")
  (list "^MH_"                                "MOUNTING"      0 "LOW")
  (list "^TS_CP[0-9]"                         "TEST_COUPON"   0 "LOW")
  (list "^TS_OCMB[0-9]"                       "TEST_COUPON"   0 "LOW")

  ;; ================================================================
  ;; Priority 1: DDR5 (GHz range = CRITICAL)
  ;; ================================================================
  ;; DDR5 reset/alert — resets are ALWAYS CRITICAL
  (list "^DDR5_.*_RESET_N$"                   "DDR5"          1 "CRITICAL")
  (list "^DDR5_.*_ALERT_N$"                   "DDR5"          1 "CRITICAL")

  ;; DDR5 DQS/CLK — differential strobes
  (list "^DDR5_.*_\\(DQS\\|WCK\\)"              "DDR5_DQS_CLK"  2 "CRITICAL")
  (list "^DDR5_.*DQS[0-9]"                   "DDR5_DQS_CLK"  2 "CRITICAL")
  (list "^DDR5_.*_CK"                         "DDR5_DQS_CLK"  2 "CRITICAL")
  (list "^DDR5_.*_CLK"                        "DDR5_DQS_CLK"  2 "CRITICAL")
  (list "^DDR5_CK"                            "DDR5_DQS_CLK"  2 "CRITICAL")

  ;; DDR5 DQ — data lines
  (list "^DDR5_.*_DQ[0-9]"                   "DDR5_DQ"       3 "CRITICAL")
  (list "^DDR5_.*_DM"                         "DDR5_DQ"       3 "CRITICAL")
  (list "^DDR5_DQ[0-9]"                       "DDR5_DQ"       3 "CRITICAL")
  (list "^DDR5_DM"                            "DDR5_DQ"       3 "CRITICAL")
  (list "^DDR5_CB"                            "DDR5_DQ"       3 "CRITICAL")

  ;; DDR5 CA/CS — command/address
  (list "^DDR5_.*_CA[0-9]"                   "DDR5_CAC"      4 "CRITICAL")
  (list "^DDR5_.*_CS"                         "DDR5_CAC"      4 "CRITICAL")
  (list "^DDR5_.*_CKE"                        "DDR5_CAC"      4 "CRITICAL")
  (list "^DDR5_.*_ODT"                        "DDR5_CAC"      4 "CRITICAL")
  (list "^DDR5_.*_PAR"                        "DDR5_CAC"      4 "CRITICAL")
  (list "^DDR5_CA[0-9]"                       "DDR5_CAC"      4 "CRITICAL")
  (list "^DDR5_CS"                            "DDR5_CAC"      4 "CRITICAL")

  ;; DDR5 generic (catches remaining DDR5 nets)
  (list "^DDR5"                               "DDR5"          5 "CRITICAL")

  ;; ================================================================
  ;; Priority 6: DDR4 (GHz range = CRITICAL)
  ;; ================================================================
  (list "^DDR4_.*DQS[0-9]"                   "DDR4_DQ"       6 "CRITICAL")
  (list "^DDR4_.*_DQS"                        "DDR4_DQ"       6 "CRITICAL")
  (list "^DDR4_.*_DQ[0-9]"                   "DDR4_DQ"       6 "CRITICAL")
  (list "^DDR4_.*_DM"                         "DDR4_DQ"       6 "CRITICAL")
  (list "^DDR4_DQ"                            "DDR4_DQ"       6 "CRITICAL")
  (list "^DDR4_.*_CLK"                        "DDR4_ADDR"     6 "CRITICAL")
  (list "^DDR4_.*_A[0-9]"                    "DDR4_ADDR"     6 "CRITICAL")
  (list "^DDR4_.*_CS"                         "DDR4_ADDR"     6 "CRITICAL")
  (list "^DDR4"                               "DDR4"          6 "CRITICAL")

  ;; DDR generic / other DDR memory
  (list "^DM_M_DIMM[0-9]"                    "DDR4_DQ"       7 "CRITICAL")
  (list "^PM_A_P_DIMM[0-9]"                  "DDR4_ADDR"     7 "CRITICAL")
  (list "^RC_MEM_"                            "DDR"           7 "CRITICAL")
  (list "_MEM_CK$"                            "DDR"           7 "CRITICAL")
  (list "^LPDDR"                              "DDR"           7 "CRITICAL")
  (list "^DDR3"                               "DDR"           7 "CRITICAL")
  (list "^DDR[0-9]"                           "DDR"           7 "CRITICAL")
  (list "^DDR_"                               "DDR"           7 "CRITICAL")

  ;; ================================================================
  ;; Priority 7-8: HBM (High Bandwidth Memory, GHz range = CRITICAL)
  ;; ================================================================
  ;; Specific HBM sub-buses first (lower priority number = checked first)
  (list "^HBM[0-9]*_DQ"                      "HBM_DQ"        7 "CRITICAL")
  (list "^HBM[0-9]*_CK"                      "HBM_CLK"       7 "CRITICAL")
  (list "^HBM[0-9]*_CA"                      "HBM_CAC"       7 "CRITICAL")
  ;; Generic HBM catch-all
  (list "^HBM"                                "HBM"           8 "CRITICAL")

  ;; ================================================================
  ;; Priority 8: PCIe (GHz = CRITICAL)
  ;; ================================================================
  (list "PERST"                               "RESET_PGOOD"   8 "CRITICAL")
  (list "_PE_WAKE_"                           "RESET_PGOOD"   8 "CRITICAL")
  (list "_PEWAKE_"                            "RESET_PGOOD"   8 "CRITICAL")
  (list "PE_WAKE"                             "RESET_PGOOD"   8 "CRITICAL")
  (list "^PE_.*CLK[0-9]*_\\(P\\|N\\)$"          "CLOCK_DIFF"    9 "CRITICAL")
  (list "REFCLK_\\(P\\|N\\)$"                   "CLOCK_DIFF"    9 "CRITICAL")
  (list "DIFF_SYSCLK.*_\\(P\\|N\\)$"            "CLOCK_DIFF"    9 "CRITICAL")
  (list "^PE_"                                "PCIE"         10 "CRITICAL")
  (list "_PE_"                                "PCIE"         10 "CRITICAL")
  (list "^PCIE"                               "PCIE"         10 "CRITICAL")
  (list "^MGT[A-Z]*\\(RX\\|TX\\)[NP][0-9]"     "PCIE"         10 "CRITICAL")

  ;; ================================================================
  ;; Priority 11: CXL / NVLink (GHz range = CRITICAL)
  ;; ================================================================
  ;; CXL (Compute Express Link) — PCIe-based, GHz differential
  (list "^CXL"                                "CXL"          11 "CRITICAL")
  ;; NVLink — GPU interconnect, GHz differential
  (list "^NVLINK"                             "NVLINK"       11 "CRITICAL")

  ;; ================================================================
  ;; Priority 11: High-speed serial (>100kHz = CRITICAL)
  ;; ================================================================
  ;; SGMII / Ethernet PHY
  (list "SGMII"                               "SGMII"        11 "CRITICAL")
  (list "^ETH[0-9]*_SMB_ALRT"                "RESET_PGOOD"  11 "CRITICAL")
  (list "^ETH[0-9]*_SMB_"                    "I2C"          11 "CRITICAL")
  (list "^ETH[0-9]*_FL_"                     "SPI"          11 "CRITICAL")
  (list "^FL_ETH[0-9]"                       "SPI"          11 "CRITICAL")
  (list "^ETH[0-9]*_PERST"                   "RESET_PGOOD"  11 "CRITICAL")
  (list "^ETH[0-9]*_"                        "ETHERNET_PHY" 11 "CRITICAL")
  (list "^MAC[0-9]*_MDI[0-9]"               "ETHERNET_PHY" 11 "CRITICAL")
  (list "^RGMII"                              "ETHERNET_PHY" 11 "CRITICAL")
  (list "^RMII"                               "ETHERNET_PHY" 11 "CRITICAL")
  (list "^MII_"                               "ETHERNET_PHY" 11 "CRITICAL")

  ;; UFS
  (list "UFS[0-9]*_"                          "UFS"          12 "CRITICAL")
  (list "^UFS_"                               "UFS"          12 "CRITICAL")
  (list "UFS"                                 "UFS"          12 "CRITICAL")

  ;; JESD204 (high-speed data converter interface, GHz = CRITICAL)
  (list "^JESD204"                            "JESD204"      12 "CRITICAL")
  (list "JESD204"                             "JESD204"      12 "CRITICAL")

  ;; USB
  (list "^USB2[A-D]_"                         "USB"          13 "CRITICAL")
  (list "^USB3"                               "USB"          13 "CRITICAL")
  (list "^USB"                                "USB"          13 "CRITICAL")

  ;; SATA / DisplayPort / HDMI / MIPI
  (list "^SATA"                               "SATA"         14 "CRITICAL")
  (list "^DP_\\(TX\\|RX\\|AUX\\|ML\\)"            "DISPLAYPORT"  14 "CRITICAL")
  (list "^EDP_"                               "DISPLAYPORT"  14 "CRITICAL")
  (list "^HDMI"                               "HDMI"         14 "CRITICAL")
  (list "^TMDS"                               "HDMI"         14 "CRITICAL")
  (list "^CSI.*_\\(D\\|CLK\\)"                  "MIPI"         14 "CRITICAL")
  (list "^DSI.*_\\(D\\|CLK\\)"                  "MIPI"         14 "CRITICAL")
  (list "^MIPI"                               "MIPI"         14 "CRITICAL")

  ;; LVDS generic (not caught by other specific protocol patterns)
  (list "LVDS"                                "LVDS"         14 "CRITICAL")

  ;; ================================================================
  ;; Priority 15: Clocks (>100kHz by definition = CRITICAL)
  ;; ================================================================
  (list "[0-9]+\\.[0-9]+MHZ"                 "CLOCK"        15 "CRITICAL")
  (list "^CLK_"                               "CLOCK"        15 "CRITICAL")
  (list "_CLK$"                               "CLOCK"        15 "CRITICAL")
  (list "_CLK_"                               "CLOCK"        15 "CRITICAL")
  (list "^CLOCK"                              "CLOCK"        15 "CRITICAL")
  (list "^SYSCLK"                             "CLOCK"        15 "CRITICAL")
  (list "^CLK[0-9]"                           "CLOCK"        15 "CRITICAL")
  (list "_CCLK$"                              "CLOCK"        15 "CRITICAL")
  (list "^FPGA_CCLK"                          "CLOCK"        15 "CRITICAL")
  (list "_SI5332$"                            "CLOCK"        15 "CRITICAL")
  (list "^XTAL"                               "CLOCK"        15 "CRITICAL")
  (list "^XTAL[0-9]*$"                       "CLOCK"        15 "CRITICAL")

  ;; ================================================================
  ;; Priority 16: SPI (10-100MHz = CRITICAL)
  ;; ================================================================
  (list "^SPI[0-9]*_"                         "SPI"          16 "CRITICAL")
  (list "^QSPI[0-9]*_"                       "SPI"          16 "CRITICAL")
  (list "_SPIROM_"                            "SPI"          16 "CRITICAL")
  (list "^BMC_SPIROM"                         "SPI"          16 "CRITICAL")
  (list "^ESPI"                               "SPI"          16 "CRITICAL")
  (list "^FWSPI"                              "SPI"          16 "CRITICAL")
  (list "_FWSPI"                              "SPI"          16 "CRITICAL")

  ;; ================================================================
  ;; Priority 17: JTAG (TCK up to 50MHz = CRITICAL)
  ;; ================================================================
  (list "^JTAG"                               "JTAG"         17 "CRITICAL")
  (list "^\\(TDI\\|TDO\\|TMS\\|TCK\\|TRST\\)"     "JTAG"         17 "CRITICAL")
  (list "_\\(TDI\\|TDO\\|TMS\\|TCK\\|TRST\\)_"    "JTAG"         17 "CRITICAL")
  (list "_\\(TDI\\|TDO\\|TMS\\|TCK\\|TRST\\)$"    "JTAG"         17 "CRITICAL")

  ;; ================================================================
  ;; Priority 18: LPC (33MHz = CRITICAL)
  ;; ================================================================
  (list "^LPC_"                               "LPC"          18 "CRITICAL")
  (list "^\\(LDRQ\\|LFRAME\\|LAD[0-9]\\|LCLK\\|LRST\\)" "LPC"    18 "CRITICAL")
  (list "_\\(LDRQ\\|LFRAME\\|LAD[0-9]\\|LCLK\\)_" "LPC"         18 "CRITICAL")

  ;; ================================================================
  ;; Priority 19: PECI (~1MHz = CRITICAL)
  ;; ================================================================
  (list "PECI"                                "PECI"         19 "CRITICAL")

  ;; ================================================================
  ;; Priority 20: MDIO (2.5MHz = CRITICAL)
  ;; ================================================================
  (list "^\\(MDC\\|MDIO\\)"                     "MDIO"         20 "CRITICAL")
  (list "_\\(MDC\\|MDIO\\)"                     "MDIO"         20 "CRITICAL")

  ;; ================================================================
  ;; Priority 16: I3C (12.5MHz+ = CRITICAL, faster successor to I2C)
  ;; ================================================================
  (list "^I3C"                                "I3C"          16 "CRITICAL")

  ;; ================================================================
  ;; Priority 21: I2C / SMBus (100kHz-1MHz = CRITICAL)
  ;; ================================================================
  (list "^I2C[0-9]*_\\(FLASH\\|LP_RESET\\)"     "RESET_PGOOD"  21 "CRITICAL")
  (list "^I2C[0-9]*_"                        "I2C"          21 "CRITICAL")
  (list "^\\(SDA\\|SCL\\)"                      "I2C"          21 "CRITICAL")
  (list "_\\(SDA\\|SCL\\)"                      "I2C"          21 "CRITICAL")
  (list "_SMB_"                               "I2C"          21 "CRITICAL")
  (list "_SMBUS"                              "I2C"          21 "CRITICAL")
  (list "DDC\\(DAT\\|CLK\\)"                    "I2C"          21 "CRITICAL")
  (list "^PMBUS"                              "I2C"          21 "CRITICAL")

  ;; ================================================================
  ;; Priority 22: Noisy switching (MHz range = CRITICAL aggressor)
  ;; ================================================================
  (list "CLK_[0-9]*VCS"                       "NOISY"        22 "CRITICAL")
  (list "_VCS_LATCH"                          "NOISY"        22 "CRITICAL")
  (list "_VCS_SENSE"                          "NOISY"        22 "CRITICAL")
  (list "_SENSE_OUT$"                         "NOISY"        22 "CRITICAL")
  (list "^SW_[0-9]"                           "NOISY"        22 "CRITICAL")
  (list "^SW_[0-9]*P[0-9]"                   "NOISY"        22 "CRITICAL")
  (list "^SW[A-D]_"                           "NOISY"        22 "CRITICAL")
  (list "^BST[A-D]_"                          "NOISY"        22 "CRITICAL")
  (list "^GATE_[0-9]"                         "NOISY"        22 "CRITICAL")
  (list "^HGATE_"                             "NOISY"        22 "CRITICAL")
  (list "^HG_R_"                              "NOISY"        22 "CRITICAL")
  (list "^FSTP_"                              "NOISY"        22 "CRITICAL")
  (list "^SENM_"                              "NOISY"        22 "CRITICAL")
  (list "^SENP[0-9]"                          "NOISY"        22 "CRITICAL")
  (list "^SET_[0-9]"                          "NOISY"        22 "CRITICAL")
  (list "^VR_FET_SW_"                         "NOISY"        22 "CRITICAL")
  (list "_SW$"                                "NOISY"        22 "CRITICAL")
  (list "^P0V[0-9]*_SW"                      "NOISY"        22 "CRITICAL")
  (list "^SW_P[0-9]V"                         "NOISY"        22 "CRITICAL")
  (list "^VIN_[0-9]*V.*_HS"                  "NOISY"        22 "CRITICAL")
  (list "^VIN_12V"                            "NOISY"        22 "CRITICAL")
  (list "^VIN_[0-9]P[0-9]*V"                 "NOISY"        22 "CRITICAL")
  (list "^D[0-9]P[0-9]_V.*_SEN"             "NOISY"        22 "CRITICAL")

  ;; ================================================================
  ;; Priority 25: Resets and enables (ALWAYS CRITICAL per user)
  ;; ================================================================
  (list ".*\\(RESET\\|RST\\)"                   "RESET_PGOOD"  25 "CRITICAL")
  (list ".*\\(PGOOD\\|PWRGD\\|PWR_OK\\)"         "RESET_PGOOD"  25 "CRITICAL")
  (list "_EN$"                                "RESET_PGOOD"  25 "CRITICAL")
  (list "_EN_"                                "RESET_PGOOD"  25 "CRITICAL")
  (list "_EN[0-9]*$"                          "RESET_PGOOD"  25 "CRITICAL")
  (list "_ENB$"                               "RESET_PGOOD"  25 "CRITICAL")
  (list "ENABLE"                              "RESET_PGOOD"  25 "CRITICAL")
  (list "^EN_"                                "RESET_PGOOD"  25 "CRITICAL")
  (list "^PG_"                                "RESET_PGOOD"  25 "CRITICAL")
  (list "_PG$"                                "RESET_PGOOD"  25 "CRITICAL")
  (list "RSTIND"                              "RESET_PGOOD"  25 "CRITICAL")
  (list "RSTB"                                "RESET_PGOOD"  25 "CRITICAL")
  (list "SRSTB"                               "RESET_PGOOD"  25 "CRITICAL")
  (list "\\(WDT\\|WATCHDOG\\)"                  "RESET_PGOOD"  25 "CRITICAL")
  (list "\\(PRES\\|PRESENCE\\)"                  "RESET_PGOOD"  25 "CRITICAL")
  (list "PRSNT"                               "RESET_PGOOD"  25 "CRITICAL")
  (list "\\(ALERT\\|ALERTB\\|ALERT_N\\)"         "RESET_PGOOD"  25 "CRITICAL")
  (list "\\(OVERCUR\\|OC[0-9]*\\)_"             "RESET_PGOOD"  25 "CRITICAL")
  (list "\\(FLT\\|FAULT\\)"                     "RESET_PGOOD"  25 "CRITICAL")
  (list "\\(IRQ\\|INT\\|NMI\\|SIRQ\\|SERIRQ\\)"   "RESET_PGOOD"  25 "CRITICAL")
  (list "POWER_ON_"                           "RESET_PGOOD"  25 "CRITICAL")
  (list "PWRON_"                              "RESET_PGOOD"  25 "CRITICAL")
  (list "_POWER_BTN"                          "RESET_PGOOD"  25 "CRITICAL")
  (list "USB_TARGET_MODE"                     "RESET_PGOOD"  25 "CRITICAL")
  (list "USB_HOST_MODE"                       "RESET_PGOOD"  25 "CRITICAL")
  (list "_DC_GOOD"                            "RESET_PGOOD"  25 "CRITICAL")
  (list "_DCGOOD"                             "RESET_PGOOD"  25 "CRITICAL")
  (list "DCGOOD$"                             "RESET_PGOOD"  25 "CRITICAL")
  (list "HOTPLUG"                             "RESET_PGOOD"  25 "CRITICAL")
  (list "WARN"                                "RESET_PGOOD"  25 "CRITICAL")
  (list "^THERM_N$"                           "RESET_PGOOD"  25 "CRITICAL")
  (list "_EPOW_N$"                            "RESET_PGOOD"  25 "CRITICAL")
  (list "ENBL_N"                              "RESET_PGOOD"  25 "CRITICAL")
  (list "MFG_.*ENBL"                          "RESET_PGOOD"  25 "CRITICAL")
  (list "VRRDY"                               "RESET_PGOOD"  25 "CRITICAL")
  (list "POWER_UP"                            "RESET_PGOOD"  25 "CRITICAL")
  (list "^PS_FFS"                             "RESET_PGOOD"  25 "CRITICAL")
  (list "^PS[0-9]*_PRES"                     "RESET_PGOOD"  25 "CRITICAL")
  (list "_NMIBTN_"                            "RESET_PGOOD"  25 "CRITICAL")
  (list "^MCLR"                               "RESET_PGOOD"  25 "CRITICAL")
  (list "_MAL_PROT_"                          "RESET_PGOOD"  25 "CRITICAL")
  (list "FPGA_.*INIT"                         "RESET_PGOOD"  25 "CRITICAL")
  (list "FPGA_.*PROGRAM"                      "RESET_PGOOD"  25 "CRITICAL")

  ;; ================================================================
  ;; Priority 26: MUX select / boot mode (resets/config = CRITICAL)
  ;; ================================================================
  (list "_MUX_SEL"                            "RESET_PGOOD"  26 "CRITICAL")
  (list "_MUX_OE"                             "RESET_PGOOD"  26 "CRITICAL")
  (list "_BOOT_MODE"                          "MISC"         26 "LOW")
  (list "_CONFIG_MODE"                        "MISC"         26 "LOW")

  ;; ================================================================
  ;; Priority 30: UART (borderline ~100kHz = MEDIUM)
  ;; ================================================================
  (list "^COM[0-9]*_"                         "UART"         30 "MEDIUM")
  (list "^UART"                               "UART"         30 "MEDIUM")
  (list "_\\(TXD\\|RXD\\|DCD\\|NRI\\)$"          "UART"         30 "MEDIUM")
  (list "^MAX32[0-9]"                         "UART"         30 "MEDIUM")

  ;; ================================================================
  ;; Priority 40: Static / slow signals (LOW)
  ;; ================================================================
  ;; GPIO — user: "static nets gpios are not [critical]"
  (list "GPIO[0-9]*[A-Z]*[0-9]"              "GPIO"         40 "LOW")
  (list "^GPIO_"                              "GPIO"         40 "LOW")
  (list "^BRD_ID[0-9]"                       "GPIO"         40 "LOW")
  (list "^RESERVE_GPIO"                       "GPIO"         40 "LOW")
  (list "^GPIO_[0-9]"                         "GPIO"         40 "LOW")
  ;; GPI — general purpose input (matches GPI anywhere, after GPIO rules)
  (list "GPI"                                 "GPIO"         40 "LOW")

  ;; LEDs
  (list "ENCLOSURE_.*LED"                     "LED"          41 "LOW")
  (list "_FAULT_LED"                          "LED"          41 "LOW")
  (list "_CARD_.*LED"                         "LED"          41 "LOW")
  (list "^LED"                                "LED"          41 "LOW")
  (list "_LED$"                               "LED"          41 "LOW")
  (list "_LED_"                               "LED"          41 "LOW")
  (list "_LED[0-9]"                           "LED"          41 "LOW")
  (list "^DS[0-9]*_"                          "LED"          41 "LOW")
  (list "HEARTBEAT"                           "LED"          41 "LOW")
  (list "^HB_"                                "LED"          41 "LOW")
  (list "_HB$"                                "LED"          41 "LOW")

  ;; Power / ground (DC = LOW)
  (list "^VBAT"                               "POWER"        42 "LOW")
  (list "^BATTERY"                            "POWER"        42 "LOW")
  (list "^\\+[0-9]"                           "POWER"        42 "LOW")
  (list "^REG[0-9]*_"                        "POWER"        42 "LOW")
  (list "^IMON_"                              "POWER"        42 "LOW")
  (list "^LDO[0-9]*_"                        "POWER"        42 "LOW")
  (list "^VDD"                                "POWER"        42 "LOW")
  (list "^VCC"                                "POWER"        42 "LOW")
  (list "^VSS"                                "GROUND"       42 "LOW")
  (list "^GND"                                "GROUND"       42 "LOW")
  (list "PMIC[0-9]"                           "POWER"        42 "LOW")

  ;; Termination reference (DC = LOW)
  (list "_TERMREF$"                           "TERMREF"      43 "LOW")
  (list "^VREF"                               "TERMREF"      43 "LOW")

  ;; ================================================================
  ;; Priority 50: Miscellaneous catch-all (LOW)
  ;; ================================================================
  (list "^ADC"                                "MISC"         50 "LOW")
  (list "^DAC"                                "MISC"         50 "LOW")
  (list "ADCREXT"                             "MISC"         50 "LOW")
  (list "DACRSET"                             "MISC"         50 "LOW")
  (list "^TPM_"                               "MISC"         50 "LOW")
  (list "^SMBADDR_"                           "MISC"         50 "LOW")
  (list "^PMIC[0-9]*_[0-9]*$"               "MISC"         50 "LOW")
  (list "^HS[IO][NP]_[0-9]"                  "MISC"         50 "LOW")
  (list "^CFGBVS"                             "MISC"         50 "LOW")
  (list "^PUDC_"                              "MISC"         50 "LOW")
  (list "^MCT[0-9]*_IO$"                     "MISC"         50 "LOW")
  (list "^PRVSN_PIC_"                         "MISC"         50 "LOW")
  (list "^ACPI"                               "MISC"         50 "LOW")
  (list "^PHR_\\(IN\\|OUT\\)"                   "MISC"         50 "LOW")
  (list "_LINK_R.*_IO$"                       "MISC"         50 "LOW")
  (list "^RACK_TOWER"                         "MISC"         50 "LOW")
  (list "RECOVERY_\\(MODE\\|IND\\)"             "MISC"         50 "LOW")
  (list "^BMC_MACLINK"                        "MISC"         50 "LOW")
  (list "FPGA_.*\\(M[0-9]\\|DONE\\|SERR\\)"     "MISC"         50 "LOW")
  (list "^DIMM[0-9]*.*_CP[0-9]*_FAIL_N$"    "MISC"         50 "LOW")
  (list "^SPARE"                              "MISC"         50 "LOW")
  (list "^RSVD"                               "MISC"         50 "LOW")

))

;; Category -> risk level fallback (if net matches no pattern)
(setq dpvc_default_risk "CRITICAL")
(setq dpvc_default_category "UNKNOWN")

(printf "  categories.il loaded: %d signal classification rules\n"
  (length dpvc_signal_categories))
