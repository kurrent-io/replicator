<template>
  <el-card class="box-card">
    <template #header>
      <div class="card-header">
        <span>Replication progress</span>
      </div>
    </template>

    <el-progress :text-inside="true" :stroke-width="26" :percentage="progress"></el-progress>
    <p></p>

    <el-row :gutter="12">
      <el-col :span="6">
        <Gauge title="Reads per sec" :value="currentReadRate"/>
      </el-col>
      <el-col :span="6">
        <Gauge title="Total reads" :value="readEvents"/>
      </el-col>
      <el-col :span="6">
        <Gauge title="Writes per sec" :value="currentWriteRate"/>
      </el-col>
      <el-col :span="6">
        <Gauge title="Total writes" :value="processedEvents"/>
      </el-col>
    </el-row>

    <el-row :gutter="12">
      <el-col :span="6">
        <Gauge title="Reader position" :value="readPosition"/>
      </el-col>
      <el-col :span="6">
        <Gauge title="Sink original position" :value="sinkPosition"/>
      </el-col>
      <el-col :span="6">
        <Gauge title="Source end position" :value="lastSourcePosition"/>
      </el-col>
      <el-col :span="6">
        <Gauge title="Processing gap" :value="gap"/>
      </el-col>
    </el-row>

    <el-row :gutter="12">
      <el-col :span="6">
        <ChannelSize name="Prepare channel" :capacity="prepareChannel.capacity" :size="prepareChannel.size"/>
      </el-col>
      <el-col :span="6">
        <Gauge title="Prepare latency" :value="`${prepareLatency} ms`"/>
      </el-col>
      <el-col :span="6">
        <ChannelSize name="Sink channel" :capacity="sinkChannel.capacity" :size="sinkChannel.size"/>
      </el-col>
      <el-col :span="6">
        <Gauge title="Sink latency" :value="`${sinkLatency} ms`"/>
      </el-col>
    </el-row>
  </el-card>
</template>

<script lang="ts">
import Gauge from "@/components/Gauge.vue";
import ChannelSize from "@/components/ChannelSize.vue";
import {useStore} from "vuex";
import {key, State} from "@/store";

export default {
    name: "Dashboard",
    components: {Gauge, ChannelSize},
    props: {
        name: String
    },
    setup(): State {
        const store = useStore(key);

        function refresh() {
            store.dispatch("getCounters");
            setTimeout(refresh, 1000);
        }

        setTimeout(refresh, 1000);

        return store.state;
    }
}
</script>

<style scoped lang="stylus">
.percentage-value
  display block
  text-align center
  margin-top 10px
  font-size 28px

.percentage-label
  display block
  text-align center
  margin-top 10px
  font-size 10px
</style>
